using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Runtime.Versioning;
using System.Runtime.ExceptionServices;

namespace NordVault.Core;

public sealed record FileCryptoProgress(string Stage, long Bytes, int? Percentage);
public sealed record FileCryptoResult(string OutputPath, long OutputBytes, string? CleanupWarning = null);

/// <summary>Uses the pinned GnuPG engine and its authenticated file format; implements no cipher.</summary>
[SupportedOSPlatform("windows")]
public sealed class FileCryptoService(GnuPgRuntime runtime)
{
    public const long MaximumFileBytes = CryptoLimits.MaximumFileBytes;
    public const int MaximumMetadataBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public Task<FileCryptoResult> EncryptAsync(string input, string output, ReadOnlyMemory<char> password, CancellationToken cancellation = default, IProgress<FileCryptoProgress>? progress = null)
        => TransformAsync(input, output, password, false, cancellation, progress);

    public Task<FileCryptoResult> DecryptAsync(string input, string output, ReadOnlyMemory<char> password, CancellationToken cancellation = default, IProgress<FileCryptoProgress>? progress = null)
        => TransformAsync(input, output, password, true, cancellation, progress);

    /// <summary>Small catalogs go directly from memory to GnuPG: no plaintext metadata file.</summary>
    public async Task EncryptMetadataAsync(ReadOnlyMemory<byte> content, string output, ReadOnlyMemory<char> password, CancellationToken cancellation = default)
    {
        if (content.Length > MaximumMetadataBytes) throw new IOException("Valvets innehållsförteckning är för stor.");
        ValidatePassword(password.Span, false); runtime.Verify(); cancellation.ThrowIfCancellationRequested();
        var destination = FileCryptoPaths.Validate(output);
        if (Path.Exists(destination)) throw new IOException("Resultatfilen finns redan.");
        var home = CreatePrivateHome();
        var part = Path.Combine(Path.GetDirectoryName(destination)!, ".nordvault-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            using (var target = CreateOutputFile(part))
            {
                FileCryptoPaths.VerifyHandlePath(target.SafeFileHandle, part);
                await RunGpgAsync("-", password, home, false, target, content.Length, cancellation, null, "Krypterar innehållsförteckning", content, MaximumMetadataBytes + 65536);
                await target.FlushAsync(cancellation); target.Flush(true);
            }
            cancellation.ThrowIfCancellationRequested(); FileCryptoPaths.Validate(destination);
            File.Move(part, destination, false);
        }
        finally
        {
            try { await StopPrivateAgentAsync(home); }
            finally { try { if (File.Exists(part)) File.Delete(part); } finally { DeletePrivateHome(home); } }
        }
    }

    public async Task<SensitiveBytes> DecryptMetadataAsync(string input, ReadOnlyMemory<char> password, CancellationToken cancellation = default)
    {
        ValidatePassword(password.Span, true); runtime.Verify(); cancellation.ThrowIfCancellationRequested();
        var source = FileCryptoPaths.Validate(input);
        using var held = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        FileCryptoPaths.VerifyHandlePath(held.SafeFileHandle, source);
        if (held.Length > MaximumMetadataBytes + 65536) throw new IOException("Innehållsförteckningen är för stor.");
        var home = CreatePrivateHome();
        using var memory = new MemoryStream();
        try
        {
            await RunGpgAsync(source, password, home, true, memory, held.Length, cancellation, null, "Öppnar valvet", outputLimit: MaximumMetadataBytes);
            return new SensitiveBytes(memory.ToArray());
        }
        finally
        {
            if (memory.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan());
            try { await StopPrivateAgentAsync(home); } finally { DeletePrivateHome(home); }
        }
    }

    /// <summary>
    /// Key pair for the login inbox (saving while the vault is locked): an OpenPGP ed25519 key with an RSA-3072 encryption subkey,
    /// made and exported by GnuPG (its ECC encryption subkeys do not survive export and import in 2.5.24). The secret key has no passphrase
    /// of its own; the caller stores it encrypted under the vault key.
    /// </summary>
    public async Task<(byte[] PublicKey, SensitiveBytes SecretKey)> CreateKeyPairAsync(string userId, CancellationToken cancellation = default)
    {
        runtime.Verify(); cancellation.ThrowIfCancellationRequested();
        var home = CreatePrivateHome();
        try
        {
            var created = new GpgStatus();
            await RunKeyGpgAsync(home, ["--passphrase", "", "--quick-gen-key", userId, "ed25519", "cert", "never"], default, Stream.Null, 0, created, cancellation);
            if (created.CreatedKey is not { Length: 40 } fingerprint) throw new InvalidOperationException("GnuPG kunde inte skapa brevlådans nyckel.");
            await RunKeyGpgAsync(home, ["--passphrase", "", "--quick-add-key", fingerprint, "rsa3072", "encr", "never"], default, Stream.Null, 0, new GpgStatus(), cancellation);
            using var publicKey = new MemoryStream();
            await RunKeyGpgAsync(home, ["--export", fingerprint], default, publicKey, 64 * 1024, new GpgStatus(), cancellation);
            using var secretKey = new MemoryStream();
            try
            {
                await RunKeyGpgAsync(home, ["--passphrase", "", "--export-secret-keys", fingerprint], default, secretKey, 64 * 1024, new GpgStatus(), cancellation);
                if (publicKey.Length == 0 || secretKey.Length == 0) throw new InvalidOperationException("GnuPG kunde inte exportera brevlådans nyckel.");
                return (publicKey.ToArray(), new SensitiveBytes(secretKey.ToArray()));
            }
            finally { if (secretKey.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan()); }
        }
        finally { try { await StopPrivateAgentAsync(home); } finally { DeletePrivateHome(home); } }
    }

    /// <summary>Encrypts a small message to a public key only (no password needed), with authenticated AES-256 (OCB).</summary>
    public async Task EncryptToKeyAsync(ReadOnlyMemory<byte> content, ReadOnlyMemory<byte> publicKey, string output, CancellationToken cancellation = default)
    {
        if (content.Length > MaximumMetadataBytes) throw new IOException("Innehållet är för stort.");
        runtime.Verify(); cancellation.ThrowIfCancellationRequested();
        var destination = FileCryptoPaths.Validate(output);
        if (Path.Exists(destination)) throw new IOException("Resultatfilen finns redan.");
        var home = CreatePrivateHome();
        var part = Path.Combine(Path.GetDirectoryName(destination)!, ".nordvault-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            var recipient = Path.Combine(home, "recipient.pgp");
            await File.WriteAllBytesAsync(recipient, publicKey.ToArray(), cancellation);
            var status = new GpgStatus();
            using (var target = CreateOutputFile(part))
            {
                FileCryptoPaths.VerifyHandlePath(target.SafeFileHandle, part);
                await RunKeyGpgAsync(home, ["--no-keyring", "--trust-model", "always", "--recipient-file", recipient, "--force-ocb", "--personal-cipher-preferences", "AES256",
                    "--compress-algo", "none", "--set-filename", "", "--encrypt", "-"], content, target, MaximumMetadataBytes + 65536, status, cancellation);
                if (status.Failed || !status.IsAuthenticatedAes256 || !status.EncryptionEnded) throw new InvalidOperationException("GnuPG kunde inte bekräfta autentiserad AES-256-kryptering.");
                await target.FlushAsync(cancellation); target.Flush(true);
            }
            cancellation.ThrowIfCancellationRequested(); FileCryptoPaths.Validate(destination);
            File.Move(part, destination, false);
        }
        finally
        {
            try { await StopPrivateAgentAsync(home); }
            finally { try { if (File.Exists(part)) File.Delete(part); } finally { DeletePrivateHome(home); } }
        }
    }

    /// <summary>Decrypts messages made by <see cref="EncryptToKeyAsync"/> with the secret key. A file that does not verify gives null.</summary>
    public async Task<List<SensitiveBytes?>> DecryptWithKeyAsync(IReadOnlyList<string> inputs, ReadOnlyMemory<byte> secretKey, int outputLimit, CancellationToken cancellation = default)
    {
        runtime.Verify(); cancellation.ThrowIfCancellationRequested();
        var home = CreatePrivateHome();
        var results = new List<SensitiveBytes?>();
        try
        {
            await RunKeyGpgAsync(home, ["--import"], secretKey, Stream.Null, 0, new GpgStatus(), cancellation);
            foreach (var input in inputs)
            {
                using var memory = new MemoryStream();
                try
                {
                    var source = FileCryptoPaths.Validate(input);
                    if (new FileInfo(source).Length > outputLimit + 65536) throw new IOException("Filen är för stor.");
                    var status = new GpgStatus();
                    await RunKeyGpgAsync(home, ["--passphrase", "", "--decrypt", "--", source], default, memory, outputLimit, status, cancellation);
                    results.Add(status.Failed || !status.IsAuthenticatedAes256 || !status.DecryptionOkay ? null : new SensitiveBytes(memory.ToArray()));
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException) { results.Add(null); }
                finally { if (memory.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan()); }
            }
            return results;
        }
        catch { foreach (var result in results) result?.Dispose(); throw; }
        finally { try { await StopPrivateAgentAsync(home); } finally { DeletePrivateHome(home); } }
    }

    /// <summary>Key operations in a private home: no passphrase line on stdin, only the given content.</summary>
    private async Task RunKeyGpgAsync(string home, string[] arguments, ReadOnlyMemory<byte> stdinContent, Stream target, long outputLimit, GpgStatus status, CancellationToken cancellation)
    {
        var start = NewStartInfo(runtime.GpgPath, home);
        foreach (var arg in new[] { "--no-options", "--homedir", home, "--agent-program", runtime.AgentPath, "--batch", "--no-tty", "--pinentry-mode", "loopback", "--no-auto-key-retrieve", "--no-auto-key-import", "--status-fd", "2", "--quiet", "--output", "-" }) start.ArgumentList.Add(arg);
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("GnuPG kunde inte startas.");
        using var cancellationRegistration = cancellation.Register(() => Kill(process));
        var copy = CopyOutputAsync(process.StandardOutput.BaseStream, target, 0, outputLimit, cancellation, null, "");
        var diagnostics = ReadStatusAsync(process.StandardError, status, cancellation);
        var exit = process.WaitForExitAsync(cancellation);
        try
        {
            if (!stdinContent.IsEmpty) await process.StandardInput.BaseStream.WriteAsync(stdinContent, cancellation);
            process.StandardInput.Close();
            var pending = new List<Task> { copy, diagnostics, exit };
            while (pending.Count > 0) { var completed = await Task.WhenAny(pending); await completed; pending.Remove(completed); }
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || status.Failed) throw new InvalidOperationException("GnuPG kunde inte slutföra nyckelåtgärden.");
        }
        catch
        {
            Kill(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
            try { await Task.WhenAll(copy, diagnostics); } catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { }
            cancellation.ThrowIfCancellationRequested();
            throw;
        }
    }

    public static void ValidatePassword(ReadOnlySpan<char> password, bool decrypt)
    {
        if (password.Length > 512) throw new ArgumentException("Lösenordet är för långt.");
        try { _ = StrictUtf8.GetByteCount(password); }
        catch (EncoderFallbackException) { throw new ArgumentException("Lösenordet innehåller ett ogiltigt Unicode-tecken."); }
        var hasNonSpace = false;
        var characters = 0;
        foreach (var rune in password.EnumerateRunes())
        {
            characters++;
            if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == System.Globalization.UnicodeCategory.Format)
                throw new ArgumentException("Lösenordet får inte innehålla kontrolltecken eller osynliga formattecken.");
            hasNonSpace |= !Rune.IsWhiteSpace(rune);
        }
        if (characters < (decrypt ? 1 : 16) || characters > 256)
            throw new ArgumentException(decrypt ? "Ange filens lösenord (högst 256 Unicode-tecken)." : "Filens lösenord måste ha 16–256 Unicode-tecken.");
        if (!hasNonSpace) throw new ArgumentException("Lösenordet får inte bara innehålla blanksteg.");
    }

    private async Task<FileCryptoResult> TransformAsync(string input, string output, ReadOnlyMemory<char> password, bool decrypt, CancellationToken cancellation, IProgress<FileCryptoProgress>? progress)
    {
        cancellation.ThrowIfCancellationRequested();
        ValidatePassword(password.Span, decrypt);
        runtime.Verify();
        var source = FileCryptoPaths.Validate(input);
        var destination = FileCryptoPaths.Validate(output);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Resultatet måste sparas som en annan fil. Originalet behålls.");
        if (!File.Exists(source)) throw new FileNotFoundException("Välj en befintlig fil.");
        if (Path.Exists(destination)) throw new IOException("Resultatfilen finns redan. Välj ett nytt filnamn; ingenting skrivs över.");
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Välj en befintlig resultatmapp.");
        using var heldInput = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        FileCryptoPaths.VerifyHandlePath(heldInput.SafeFileHandle, source);
        if (heldInput.Length > MaximumFileBytes + (decrypt ? 64L * 1024 * 1024 : 0)) throw new IOException("Den här versionen stöder filer upp till 64 GiB.");
        var privateHome = CreatePrivateHome();
        var temporary = Path.Combine(parent, ".nordvault-" + Guid.NewGuid().ToString("N") + ".part");
        var committed = false;
        FileCryptoResult? result = null;
        Exception? operationError = null;
        try
        {
            if (decrypt)
            {
                // First verify the complete locked input with plaintext sent to the null sink.
                // A damaged or wrong-password file therefore never creates a plaintext result.
                progress?.Report(new("Kontrollerar lösenord och filens integritet", 0, null));
                await RunGpgAsync(source, password, privateHome, true, Stream.Null, heldInput.Length, cancellation, progress, "Verifierar filen");
            }
            cancellation.ThrowIfCancellationRequested();
            FileCryptoPaths.AssertNoReparsePoints(destination);
            using (var resultStream = CreateOutputFile(temporary))
            {
                FileCryptoPaths.VerifyHandlePath(resultStream.SafeFileHandle, temporary);
                await RunGpgAsync(source, password, privateHome, decrypt, resultStream, heldInput.Length, cancellation, progress, decrypt ? "Sparar dekrypterad fil" : "Krypterar filen");
                await resultStream.FlushAsync(cancellation);
                resultStream.Flush(flushToDisk: true);
                FileCryptoPaths.VerifyHandlePath(resultStream.SafeFileHandle, temporary);
            }
            cancellation.ThrowIfCancellationRequested();
            FileCryptoPaths.Validate(destination);
            File.Move(temporary, destination, overwrite: false);
            committed = true;
            var size = new FileInfo(destination).Length;
            progress?.Report(new("Klart · originalfilen finns kvar", size, 100));
            result = new(destination, size);
        }
        catch (Exception error) { operationError = error; }

        // Attempt every cleanup step even if stopping the private agent fails.
        // A committed result remains valid; report residual data rather than claiming failure.
        var cleanupErrors = new List<string>();
        try { await StopPrivateAgentAsync(privateHome); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        { cleanupErrors.Add("GnuPG:s privata agent kunde inte bekräftas som stoppad."); }
        try
        {
            if (!committed && File.Exists(temporary)) File.Delete(temporary); // Only the uniquely created partial file.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { cleanupErrors.Add("Den ofärdiga filen kunde inte tas bort: " + temporary + (decrypt ? " Den kan innehålla klartext." : "")); }
        try { DeletePrivateHome(privateHome); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { cleanupErrors.Add("GnuPG:s arbetsmapp kunde inte tas bort: " + privateHome); }
        var warning = cleanupErrors.Count == 0 ? null : string.Join("\n", cleanupErrors);
        if (operationError is not null)
        {
            if (warning is not null) throw new IOException("Filåtgärden misslyckades eller avbröts. Originalfilen finns kvar.\n" + warning, operationError);
            ExceptionDispatchInfo.Capture(operationError).Throw();
        }
        return result! with { CleanupWarning = warning };
    }

    private async Task RunGpgAsync(string source, ReadOnlyMemory<char> password, string home, bool decrypt, Stream target, long inputBytes, CancellationToken cancellation, IProgress<FileCryptoProgress>? progress, string stage, ReadOnlyMemory<byte> stdinContent = default, long? outputLimit = null)
    {
        var start = NewStartInfo(runtime.GpgPath, home);
        foreach (var arg in new[] { "--no-options", "--homedir", home, "--agent-program", runtime.AgentPath, "--batch", "--no-tty", "--pinentry-mode", "loopback", "--passphrase-fd", "0", "--no-symkey-cache", "--no-keyring", "--no-auto-key-retrieve", "--no-auto-key-import", "--status-fd", "2", "--quiet", "--output", "-" }) start.ArgumentList.Add(arg);
        if (decrypt) start.ArgumentList.Add("--decrypt");
        else
            foreach (var arg in new[] { "--personal-cipher-preferences", "AES256", "--use-ocb-sym", "--s2k-mode", "3", "--s2k-digest-algo", "SHA512", "--s2k-count", "65011712", "--compress-algo", "none", "--chunk-size", "16", "--set-filename", "", "--symmetric" }) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("--"); start.ArgumentList.Add(source);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("GnuPG kunde inte startas.");
        using var cancellationRegistration = cancellation.Register(() => Kill(process));
        var status = new GpgStatus();
        // The packaged engine is 32-bit: its --max-output parser cannot represent 64 GiB.
        // Bound the actual decoded stream here instead, killing the child on overflow.
        var copy = CopyOutputAsync(process.StandardOutput.BaseStream, target, inputBytes, outputLimit ?? (decrypt ? MaximumFileBytes : MaximumFileBytes + 64L * 1024 * 1024), cancellation, progress, stage);
        var diagnostics = ReadStatusAsync(process.StandardError, status, cancellation);
        var exit = process.WaitForExitAsync(cancellation);
        var passBytes = new byte[StrictUtf8.GetByteCount(password.Span) + 1];
        try
        {
            StrictUtf8.GetBytes(password.Span, passBytes.AsSpan(0, passBytes.Length - 1));
            passBytes[^1] = (byte)'\n';
            await process.StandardInput.BaseStream.WriteAsync(passBytes, cancellation);
            if (source == "-") await process.StandardInput.BaseStream.WriteAsync(stdinContent, cancellation);
            process.StandardInput.Close();
            var pending = new List<Task> { copy, diagnostics, exit };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                await completed; // A pipe/disk write failure is handled immediately, preventing deadlock.
                pending.Remove(completed);
            }
            cancellation.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || status.Failed || !status.IsAuthenticatedAes256 || (decrypt ? !status.DecryptionOkay : !status.EncryptionEnded))
                throw new InvalidOperationException(decrypt ? "Filen kunde inte verifieras. Lösenordet kan vara fel, filen skadad eller krypteringen inkompatibel. Ingen resultatfil har godkänts." : "GnuPG kunde inte bekräfta autentiserad AES-256-kryptering. Ingen resultatfil har godkänts.");
        }
        catch
        {
            Kill(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { }
            try { await Task.WhenAll(copy, diagnostics); } catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { }
            cancellation.ThrowIfCancellationRequested();
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(passBytes); }
    }

    private ProcessStartInfo NewStartInfo(string program, string home)
    {
        var start = new ProcessStartInfo(program)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = runtime.BinPath,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment.Remove("GNUPGHOME"); start.Environment.Remove("GPG_AGENT_INFO");
        start.Environment["GNUPGHOME"] = home;
        return start;
    }

    private static async Task CopyOutputAsync(Stream input, Stream output, long sourceBytes, long outputLimit, CancellationToken cancellation, IProgress<FileCryptoProgress>? progress, string stage)
    {
        var buffer = new byte[65536];
        long copied = 0;
        var previous = -1;
        try
        {
            int count;
            while ((count = await input.ReadAsync(buffer, cancellation)) != 0)
            {
                copied = checked(copied + count);
                if (copied > outputLimit) throw new IOException("Filresultatet överskrider storleksgränsen.");
                await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                var percent = sourceBytes > 0 ? (int)Math.Min(99, copied * 100 / sourceBytes) : 0;
                if (percent != previous) { progress?.Report(new(stage, copied, percent)); previous = percent; }
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static async Task ReadStatusAsync(StreamReader reader, GpgStatus status, CancellationToken cancellation)
    {
        while (await reader.ReadLineAsync(cancellation) is { } line)
        {
            // Keep only structured status flags; raw diagnostics and embedded filenames are discarded.
            if (!line.StartsWith("[GNUPG:] ", StringComparison.Ordinal)) continue;
            var fields = line[9..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;
            switch (fields[0])
            {
                case "BEGIN_ENCRYPTION": case "DECRYPTION_INFO":
                    status.IsAuthenticatedAes256 = fields.Length >= 4 && fields[2] == "9" && fields[3] == "2";
                    if (!status.IsAuthenticatedAes256 || ++status.EncryptionStreams != 1) status.Failed = true;
                    break;
                case "END_ENCRYPTION": status.EncryptionEnded = true; break;
                case "DECRYPTION_OKAY": status.DecryptionOkay = true; break;
                case "KEY_CREATED": if (fields.Length >= 3) status.CreatedKey = fields[2]; break;
                case "ERROR": case "FAILURE": case "BADMDC": case "DECRYPTION_FAILED": status.Failed = true; break;
            }
        }
    }

    private static string CreatePrivateHome()
    {
        var path = Path.Combine(Path.GetTempPath(), "NordVault-Gpg-" + Guid.NewGuid().ToString("N"));
        var security = new DirectorySecurity();
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("Windows-användaren kunde inte identifieras.");
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(sid);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
        File.WriteAllText(Path.Combine(path, "gpg-agent.conf"), "allow-loopback-pinentry\nno-allow-external-cache\ndefault-cache-ttl 0\nmax-cache-ttl 0\n", new UTF8Encoding(false));
        return path;
    }

    private static FileStream CreateOutputFile(string path)
    {
        if (!string.Equals(new DriveInfo(Path.GetPathRoot(path)!).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        var sid = identity.User ?? throw new InvalidOperationException("Windows-användaren kunde inte identifieras.");
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); security.SetOwner(sid);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough, security);
    }

    private async Task StopPrivateAgentAsync(string home)
    {
        var start = NewStartInfo(runtime.GpgConfPath, home);
        foreach (var arg in new[] { "--homedir", home, "--kill", "gpg-agent" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start);
        if (process is null) return;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { Kill(process); throw; }
        await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(10));
        if (process.ExitCode != 0) throw new IOException("Den privata GnuPG-agenten kunde inte stoppas.");
    }

    private static void DeletePrivateHome(string home)
    {
        var full = Path.GetFullPath(home);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("NordVault-Gpg-", StringComparison.Ordinal))
            throw new IOException("GnuPG:s arbetsmapp kunde inte kontrolleras för städning.");
        FileCryptoPaths.AssertNoReparsePoints(full);
        Directory.Delete(full, recursive: true); // The fresh, private, verified operation directory only.
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }

    private sealed class GpgStatus
    {
        public bool IsAuthenticatedAes256;
        public bool EncryptionEnded;
        public bool DecryptionOkay;
        public bool Failed;
        public int EncryptionStreams;
        public string? CreatedKey;
    }
}
