using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NordVault.Core;

/// <summary>Envelope around established GnuPG ciphertext. Footer hash is transport integrity, not authentication.</summary>
[SupportedOSPlatform("windows")]
public sealed class ArchivePackage(GnuPgRuntime runtime)
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("NORDVAULT-SFX03!");
    public const int FooterBytes = 64;

    public async Task<string> CreateAsync(IReadOnlyList<string> sources, string output, ReadOnlyMemory<char> password, string readerTemplate, string templateSha256, CancellationToken cancellation = default, IProgress<FileCryptoProgress>? progress = null)
    {
        if (sources.Count is < 1 or > 5000) throw new IOException("Välj 1–5 000 filer för arkivet.");
        var items = Expand(sources, cancellation);
        FileCryptoService.ValidatePassword(password.Span, false);
        output = FileCryptoPaths.Validate(output); readerTemplate = FileCryptoPaths.Validate(readerTemplate);
        if (Path.Exists(output)) throw new IOException("Arkivfilen finns redan. Ingenting skrivs över.");
        using var heldTemplate = new FileStream(readerTemplate, FileMode.Open, FileAccess.Read, FileShare.Read);
        FileCryptoPaths.VerifyHandlePath(heldTemplate.SafeFileHandle, readerTemplate);
        if (templateSha256.Length != 64 || !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(heldTemplate,cancellation), Convert.FromHexString(templateSha256)))
            throw new IOException("Den portabla läsaren matchar inte det verifierade bygget.");
        heldTemplate.Position = 0;
        using var work = new PrivateWorkspace(); Directory.CreateDirectory(Path.Combine(work.Path, "entries"));
        var crypto = new FileCryptoService(runtime); var catalog = VaultCatalog.Empty();
        var entries = new List<VaultEntry>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (sourcePath, relative) in items)
        {
            cancellation.ThrowIfCancellationRequested(); var source = FileCryptoPaths.Validate(sourcePath);
            FileStream held;
            try { held = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read); }
            catch (IOException) when (File.Exists(source)) { throw new IOException("Filen används av ett annat program: " + source); }
            using var _ = held;
            FileCryptoPaths.VerifyHandlePath(held.SafeFileHandle, source);
            var id = Guid.NewGuid().ToString("N"); var cipher = Path.Combine(work.Path, "entries", id + ".gpg");
            string name;
            if (relative is null) { var originalName = Path.GetFileName(source); if (!VaultCatalog.IsSafeName(originalName)) throw new IOException("Ett filnamn stöds inte av arkivet."); name = UniqueName(originalName, names); }
            else if (!names.Add(relative)) throw new IOException("Två valda mappar har samma namn. Välj dem en i taget.");
            else name = relative;
            progress?.Report(new($"Krypterar arkivfil {entries.Count + 1} av {items.Count}", 0, null));
            var result = await crypto.EncryptAsync(source, cipher, password, cancellation);
            if (result.CleanupWarning is not null) throw new IOException(result.CleanupWarning);
            entries.Add(new(id, name, held.Length, await VaultSession.HashAsync(cipher,cancellation), DateTimeOffset.UtcNow));
        }
        catalog = catalog with { Entries = entries.ToArray() };
        var encoded = JsonSerializer.SerializeToUtf8Bytes(catalog);
        try { await crypto.EncryptMetadataAsync(encoded, Path.Combine(work.Path, "catalog.gpg"), password, cancellation); }
        finally { CryptographicOperations.ZeroMemory(encoded); }
        var part = Path.Combine(Path.GetDirectoryName(output)!, ".nordvault-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using (var target = new FileStream(part, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.WriteThrough))
            {
                FileCryptoPaths.VerifyHandlePath(target.SafeFileHandle, part);
                await heldTemplate.CopyToAsync(target, cancellation);
                var offset = target.Position;
                // ZIP is only an envelope of already encrypted objects. ZIP names are random IDs.
                using (var segment = new OffsetWriteStream(target,offset))
                using (var zip = new ZipArchive(segment, ZipArchiveMode.Create, true))
                {
                    await AddCipher(zip, Path.Combine(work.Path, "catalog.gpg"), "catalog.gpg", cancellation);
                    foreach (var entry in catalog.Entries) await AddCipher(zip, Path.Combine(work.Path, "entries", entry.Id + ".gpg"), "entries/" + entry.Id + ".gpg", cancellation);
                }
                var length = target.Position - offset;
                // All ZIP offsets are relative to the payload, never to executable bytes.
                target.Position = offset;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536]; long remaining = length;
                while (remaining > 0) { var read = await target.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,remaining)), cancellation); if (read == 0) throw new EndOfStreamException(); hash.AppendData(buffer,0,read); remaining -= read; }
                var digest = hash.GetHashAndReset();
                var footer = new byte[FooterBytes]; Magic.CopyTo(footer,0); BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(16),offset); BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(24),length); digest.CopyTo(footer,32);
                await target.WriteAsync(footer,cancellation); await target.FlushAsync(cancellation); target.Flush(true);
            }
            cancellation.ThrowIfCancellationRequested(); FileCryptoPaths.Validate(output); File.Move(part, output, false);
            return output;
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }

    /// <summary>Files stay as they are. A folder becomes all its files, named "Folder/sub/file" so the tree is restored on unpacking.</summary>
    private static List<(string Path, string? Relative)> Expand(IReadOnlyList<string> sources, CancellationToken cancellation)
    {
        var items = new List<(string, string?)>();
        foreach (var source in sources)
        {
            if (!Directory.Exists(source)) { items.Add((source, null)); continue; }
            var folder = FileCryptoPaths.Validate(source).TrimEnd(Path.DirectorySeparatorChar); var top = Path.GetFileName(folder);
            if (!VaultCatalog.IsSafeName(top)) throw new IOException("Mappens namn stöds inte av arkivet. Välj en mapp, inte en hel disk.");
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, ReturnSpecialDirectories = false, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Offline };
            var before = items.Count;
            foreach (var file in Directory.EnumerateFiles(folder, "*", options))
            {
                cancellation.ThrowIfCancellationRequested();
                var relative = top + "/" + Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');
                if (!VaultCatalog.IsSafeRelativeName(relative)) throw new IOException("Ett namn i mappen stöds inte av arkivet: " + relative);
                items.Add((file, relative));
                if (items.Count > 5000) throw new IOException("Arkivet kan ha högst 5 000 filer. Dela upp mappen.");
            }
            if (items.Count == before) throw new IOException("Mappen är tom: " + folder);
        }
        if (items.Count > 5000) throw new IOException("Arkivet kan ha högst 5 000 filer. Dela upp mappen.");
        return items;
    }
    private static string UniqueName(string name, HashSet<string> names)
    {
        var candidate = name; var n = 1;
        while (!names.Add(candidate))
        { candidate = Path.GetFileNameWithoutExtension(name) + $" ({++n})" + Path.GetExtension(name); if (!VaultCatalog.IsSafeName(candidate)) throw new IOException("Dubbla långa filnamn kan inte särskiljas."); }
        return candidate;
    }
    private static async Task AddCipher(ZipArchive zip, string path, string name, CancellationToken cancellation)
    { var entry = zip.CreateEntry(name, CompressionLevel.NoCompression); using var output = entry.Open(); using var input = File.OpenRead(path); await input.CopyToAsync(output,cancellation); }

    public async Task<string> ExtractAsync(string package, string parent, ReadOnlyMemory<char> password, CancellationToken cancellation = default, IProgress<FileCryptoProgress>? progress = null)
    {
        package = FileCryptoPaths.Validate(package); parent = FileCryptoPaths.Validate(parent).TrimEnd('\\');
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Välj en befintlig resultatmapp.");
        using var input = new FileStream(package,FileMode.Open,FileAccess.Read,FileShare.Read);
        FileCryptoPaths.VerifyHandlePath(input.SafeFileHandle,package);
        var (offset,length) = await ValidateEnvelopeAsync(input,cancellation);
        using var work = new PrivateWorkspace(); Directory.CreateDirectory(Path.Combine(work.Path,"entries"));
        using var zipSource = new BoundedReadStream(input,offset,length);
        using var zip = new ZipArchive(zipSource,ZipArchiveMode.Read,true);
        var index = SafeZip.Index(zip);
        if (!index.TryGetValue("catalog.gpg",out var catalogEntry) || catalogEntry.Length > FileCryptoService.MaximumMetadataBytes+65536)
            throw new IOException("Arkivets krypterade innehållsförteckning saknas eller är för stor.");
        await CopyCipher(catalogEntry,Path.Combine(work.Path,"catalog.gpg"),FileCryptoService.MaximumMetadataBytes+65536,cancellation);
        var crypto = new FileCryptoService(runtime);
        using var encoded = await crypto.DecryptMetadataAsync(Path.Combine(work.Path,"catalog.gpg"),password,cancellation);
        var catalog = VaultCatalog.Read(encoded.Memory.Span, allowFolders: true);
        if (index.Count != catalog.Entries.Length+1 || catalog.Entries.Length == 0) throw new IOException("Arkivets objekt matchar inte innehållsförteckningen.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in catalog.Entries)
        {
            if (!names.Add(entry.Name) || !index.TryGetValue("entries/"+entry.Id+".gpg",out var ciphertext)) throw new IOException("Arkivet har saknade eller dubbla filer.");
            var cipher = Path.Combine(work.Path,"entries",entry.Id+".gpg");
            await CopyCipher(ciphertext,cipher,FileCryptoService.MaximumFileBytes+64L*1024*1024,cancellation);
            if (await VaultSession.HashAsync(cipher,cancellation) != entry.CipherSha256) throw new IOException("Arkivets krypterade fil matchar inte dess autentiserade innehållsförteckning.");
        }
        using var plaintext = new PrivateWorkspace(parent);
        var ready = Path.Combine(plaintext.Path,"ready"); Directory.CreateDirectory(ready);
        var destination = Path.Combine(parent,"NordVault-uppackat-"+Guid.NewGuid().ToString("N")[..8]);
        foreach(var entry in catalog.Entries)
        {
            progress?.Report(new("Packar upp "+entry.Name,0,null));
            var output = Path.GetFullPath(Path.Combine(ready,entry.Name.Replace('/',Path.DirectorySeparatorChar)));
            if (!output.StartsWith(Path.GetFullPath(ready)+"\\",StringComparison.OrdinalIgnoreCase)) throw new IOException("Arkivet innehåller en ogiltig sökväg.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!); output = FileCryptoPaths.Validate(output);
            var result = await crypto.DecryptAsync(Path.Combine(work.Path,"entries",entry.Id+".gpg"),output,password,cancellation);
            if (result.OutputBytes != entry.Size || result.CleanupWarning is not null) throw new IOException("Filens storlek eller städning kunde inte verifieras.");
        }
        cancellation.ThrowIfCancellationRequested(); FileCryptoPaths.Validate(destination);
        Directory.Move(ready,destination);
        return destination;
    }

    private static async Task CopyCipher(ZipArchiveEntry entry,string destination,long maximum,CancellationToken cancellation)
    {
        if (entry.Length > maximum) throw new IOException("Ett krypterat arkivobjekt är för stort.");
        using var source = entry.Open(); using var target = new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        await SafeZip.CopyBoundedAsync(source,target,maximum,cancellation);
        if (target.Length != entry.Length) throw new IOException("Arkivobjektets storlek stämmer inte.");
    }

    internal static async Task<(long Offset,long Length)> ValidateEnvelopeAsync(FileStream input,CancellationToken cancellation)
    {
        if (input.Length < FooterBytes+2) throw new IOException("Det här är inte ett NordVault-arkiv.");
        var footer = new byte[FooterBytes]; input.Position=input.Length-FooterBytes; await input.ReadExactlyAsync(footer,cancellation);
        if (!footer.AsSpan(0,Magic.Length).SequenceEqual(Magic)) throw new IOException("Arkivsignaturen saknas.");
        var offset=BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(16)); var length=BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(24));
        if (offset < 2 || length < 22 || offset > input.Length-FooterBytes || length != input.Length-FooterBytes-offset) throw new IOException("Arkivgränserna är ogiltiga.");
        input.Position=offset; using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer=new byte[65536]; var remaining=length;
        while(remaining>0) { var read=await input.ReadAsync(buffer.AsMemory(0,(int)Math.Min(buffer.Length,remaining)),cancellation); if(read==0) throw new EndOfStreamException(); hash.AppendData(buffer,0,read); remaining-=read; }
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(),footer.AsSpan(32,32))) throw new IOException("Arkivet är skadat eller ändrat.");
        return(offset,length);
    }
}
