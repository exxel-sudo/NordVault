using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using NordVault.Core;

var root = Directory.GetCurrentDirectory();
var runtime = new GnuPgRuntime(Path.Combine(root, "tools", "gnupg"));
var crypto = new FileCryptoService(runtime);
var scratch = Path.Combine(root, "artifacts", "ft-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var password = "Min-långa-lösenfras-åäö-🔐".ToCharArray();
var tests = new List<(string Name, Func<Task> Run)>();
var externalHomeNumber = 0;
void Add(string name, Func<Task> test) => tests.Add((name, test));
void Expect(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
string At(string name) => Path.Combine(scratch, name);
async Task Reject(Func<Task> test)
{
    try { await test(); }
    catch (Exception e) when (e is InvalidOperationException or IOException or ArgumentException or OperationCanceledException) { return; }
    throw new InvalidOperationException("Osäker eller skadad indata accepterades.");
}
void NoParts() => Expect(!Directory.EnumerateFiles(scratch, ".nordvault-*.part").Any(), "En del-fil blev kvar efter fel.");

var source = At("privat svensk testfil.txt");
var original = Encoding.UTF8.GetBytes("Svensk text · åäö · testdata, inte användarens filer.\n");
await File.WriteAllBytesAsync(source, original);
var encrypted = At("privat svensk testfil.gpg");

Add("Pinned runtime verifies all executable dependencies", () => { runtime.Verify(); return Task.CompletedTask; });
Add("Real AES-256/OCB roundtrip with Unicode password", async () =>
{
    var encryptedResult = await crypto.EncryptAsync(source, encrypted, password);
    var decryptedResult = await crypto.DecryptAsync(encrypted, At("roundtrip.txt"), password);
    Expect(encryptedResult.CleanupWarning is null && decryptedResult.CleanupWarning is null, "Arbetsdata kunde inte städas.");
    Expect((await File.ReadAllBytesAsync(At("roundtrip.txt"))).SequenceEqual(original), "Roundtrip ändrade filens bytes.");
    Expect((await File.ReadAllBytesAsync(source)).SequenceEqual(original), "Originalfilen ändrades.");
});
Add("Same input and password produce different ciphertext", async () =>
{
    await crypto.EncryptAsync(source, At("randomized.gpg"), password);
    var first = await File.ReadAllBytesAsync(encrypted);
    var second = await File.ReadAllBytesAsync(At("randomized.gpg"));
    Expect(!first.SequenceEqual(second), "Krypteringen återanvände samma krypteringsmaterial.");
});
Add("Metadata goes directly through stdin and roundtrips without plaintext file", async () =>
{
    var data = Encoding.UTF8.GetBytes("{\"private\":\"svenska åäö \\n secret metadata\"}");
    await crypto.EncryptMetadataAsync(data, At("catalog.gpg"), password);
    using var decrypted = await crypto.DecryptMetadataAsync(At("catalog.gpg"), password);
    Expect(decrypted.Memory.Span.SequenceEqual(data), "Stdin-lösenord eller metadata blandades ihop.");
    await Reject(async () => { using var wrong = await crypto.DecryptMetadataAsync(At("catalog.gpg"), "Wrong-passphrase-here".AsMemory()); });
});
Add("Empty files authenticate and roundtrip", async () =>
{
    await File.WriteAllBytesAsync(At("empty"), []);
    await crypto.EncryptAsync(At("empty"), At("empty.gpg"), password);
    await crypto.DecryptAsync(At("empty.gpg"), At("empty.out"), password);
    Expect(new FileInfo(At("empty.out")).Length == 0, "Tom fil fick annat innehåll.");
});
Add("Multiple OCB chunks preserve exact binary data", async () =>
{
    var content = RandomNumberGenerator.GetBytes(3 * 65536 + 457);
    await File.WriteAllBytesAsync(At("binary"), content);
    await crypto.EncryptAsync(At("binary"), At("binary.gpg"), password);
    await crypto.DecryptAsync(At("binary.gpg"), At("binary.out"), password);
    Expect((await File.ReadAllBytesAsync(At("binary.out"))).SequenceEqual(content), "Chunkgränsen ändrade data.");
});
Add("Wrong password publishes no plaintext", async () =>
{
    await Reject(() => crypto.DecryptAsync(encrypted, At("wrong.out"), "Fel-lösenfras-med-16-tecken".AsMemory()));
    Expect(!File.Exists(At("wrong.out")), "Fel lösenord skapade en resultatfil."); NoParts();
});
Add("Tampered ciphertext publishes no plaintext", async () =>
{
    var bytes = await File.ReadAllBytesAsync(At("binary.gpg")); bytes[bytes.Length / 2] ^= 1;
    await File.WriteAllBytesAsync(At("tampered.gpg"), bytes);
    await Reject(() => crypto.DecryptAsync(At("tampered.gpg"), At("tampered.out"), password));
    Expect(!File.Exists(At("tampered.out")), "Manipulerad fil godkändes."); NoParts();
});
Add("Truncated final authentication tag is rejected", async () =>
{
    var bytes = await File.ReadAllBytesAsync(At("binary.gpg"));
    foreach (var removed in new[] { 1, 16, 31, bytes.Length / 2 })
    {
        var input = At($"truncated-{removed}.gpg"); var output = At($"truncated-{removed}.out");
        await File.WriteAllBytesAsync(input, bytes.AsMemory(0, bytes.Length - removed).ToArray());
        await Reject(() => crypto.DecryptAsync(input, output, password));
        Expect(!File.Exists(output), "Avklippt fil godkändes.");
    }
    NoParts();
});
Add("Existing destination is never overwritten", async () =>
{
    var existing = At("keep.txt"); await File.WriteAllTextAsync(existing, "test-owned existing data");
    await Reject(() => crypto.EncryptAsync(source, existing, password));
    await Reject(() => crypto.DecryptAsync(encrypted, existing, password));
    Expect(await File.ReadAllTextAsync(existing) == "test-owned existing data", "En befintlig fil ändrades.");
});
Add("Input cannot also be the output", async () =>
{
    await Reject(() => crypto.EncryptAsync(source, source, password));
    Expect((await File.ReadAllBytesAsync(source)).SequenceEqual(original), "Originalfilen ändrades.");
});
Add("Source is held against concurrent writers", async () =>
{
    var writerBlocked = false;
    var report = new InlineProgress(_ =>
    {
        try { using var writer = File.Open(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
        catch (IOException) { writerBlocked = true; }
    });
    await crypto.EncryptAsync(source, At("held.gpg"), password, progress: report);
    Expect(writerBlocked, "Källfilen tillät en samtidig skrivare.");
});
Add("Cancellation removes partial output and preserves source", async () =>
{
    using var cancel = new CancellationTokenSource();
    var report = new InlineProgress(p => { if (p.Bytes > 0) cancel.Cancel(); });
    await Reject(() => crypto.EncryptAsync(At("binary"), At("cancel.gpg"), password, cancel.Token, report));
    Expect(!File.Exists(At("cancel.gpg")), "Avbruten åtgärd publicerade en fil."); NoParts();
});
Add("Cancellation while writing authenticated plaintext removes partial file", async () =>
{
    using var cancel = new CancellationTokenSource();
    var report = new InlineProgress(p => { if (p.Stage == "Sparar dekrypterad fil" && p.Bytes > 0) cancel.Cancel(); });
    await Reject(() => crypto.DecryptAsync(At("binary.gpg"), At("cancel-plain.txt"), password, cancel.Token, report));
    Expect(!File.Exists(At("cancel-plain.txt")), "Avbruten dekryptering publicerade klartext."); NoParts();
});
Add("A destination created during encryption is preserved atomically", async () =>
{
    var output = At("raced.gpg");
    var report = new InlineProgress(p => { if (p.Bytes > 0 && !File.Exists(output)) File.WriteAllText(output, "concurrent test-owned destination"); });
    await Reject(() => crypto.EncryptAsync(source, output, password, progress: report));
    Expect(await File.ReadAllTextAsync(output) == "concurrent test-owned destination", "En samtidig resultatfil skrevs över."); NoParts();
});
Add("Output decrypts with GnuPG invoked outside NordVault", async () =>
{
    var bytes = await ExternalGpg(encrypted, "--decrypt");
    Expect(bytes.SequenceEqual(original), "Extern GnuPG kunde inte läsa den producerade filen.");
});
Add("Produced packets contain salted iterated SHA-512 at maximum S2K count", async () =>
{
    var packets = Encoding.UTF8.GetString(await ExternalGpg(encrypted, "--list-packets"));
    Expect(packets.Contains("s2k 3, hash 10") && packets.Contains("65011712 (255)"), "Härledningens paketinställningar avviker: " + packets);
});
Add("Classic non-AEAD format is rejected", async () =>
{
    var bytes = await ExternalGpg(source, "--rfc4880", "--cipher-algo", "AES256", "--symmetric");
    await File.WriteAllBytesAsync(At("classic.gpg"), bytes);
    await Reject(() => crypto.DecryptAsync(At("classic.gpg"), At("classic.out"), password));
    Expect(!File.Exists(At("classic.out")), "Ett äldre format godkändes som AEAD."); NoParts();
});
Add("Authenticated AES-128 is rejected by AES-256 policy", async () =>
{
    var bytes = await ExternalGpg(source, "--use-ocb-sym", "--personal-cipher-preferences", "AES128", "--symmetric");
    await File.WriteAllBytesAsync(At("aes128.gpg"), bytes);
    await Reject(() => crypto.DecryptAsync(At("aes128.gpg"), At("aes128.out"), password));
    Expect(!File.Exists(At("aes128.out")), "Fel algoritm godkändes."); NoParts();
});
Add("Concatenated encrypted streams are rejected", async () =>
{
    var bytes = await File.ReadAllBytesAsync(encrypted);
    await File.WriteAllBytesAsync(At("concatenated.gpg"), bytes.Concat(bytes).ToArray());
    await Reject(() => crypto.DecryptAsync(At("concatenated.gpg"), At("concatenated.out"), password));
    Expect(!File.Exists(At("concatenated.out")), "Flera strömmar godkändes som en fil."); NoParts();
});
Add("Unsafe path syntax is rejected before file processing", async () =>
{
    foreach (var path in new[] { "relative.gpg", @"\\server\share\file.gpg", source + ":stream", At("unsafe*.gpg"), At("CON.gpg"), At("trailing.gpg ") })
        await Reject(() => crypto.EncryptAsync(source, path, password));
});
Add("Short, control and malformed Unicode passwords rejected", async () =>
{
    foreach (var secret in new[] { "short", new string(' ',16), "Long-password-with\nnewline", "Long-password-with\u200bformat", "Long-password-with\U000E0001format", "Long-password-with\ud800surrogate", string.Concat(Enumerable.Repeat("😀", 8)) })
        await Reject(() => crypto.EncryptAsync(source, At("invalid.gpg"), secret.AsMemory()));
    Expect(!File.Exists(At("invalid.gpg")), "Ogiltigt lösenord gav resultat.");
});
Add("Modified GnuPG executable is rejected without execution", async () =>
{
    var copied = Path.Combine(scratch, "modified-runtime"); Directory.CreateDirectory(Path.Combine(copied,"bin"));
    foreach (var file in Directory.EnumerateFiles(Path.Combine(root,"tools","gnupg","bin"))) File.Copy(file, Path.Combine(copied,"bin",Path.GetFileName(file)));
    var executable = Path.Combine(copied, "bin", "gpg.exe");
    using (var stream = new FileStream(executable, FileMode.Open, FileAccess.ReadWrite)) { stream.Position = stream.Length - 1; var value = stream.ReadByte(); stream.Position--; stream.WriteByte((byte)(value ^ 1)); }
    await Reject(() => new FileCryptoService(new GnuPgRuntime(copied)).EncryptAsync(source, At("modified.gpg"), password));
    Expect(!File.Exists(At("modified.gpg")), "En ändrad motor kördes.");
});
Add("An injected DLL in the runtime blocks encryption", async () =>
{
    var copied = Path.Combine(scratch, "injected-runtime"); Directory.CreateDirectory(Path.Combine(copied,"bin"));
    foreach (var file in Directory.EnumerateFiles(runtime.BinPath)) File.Copy(file, Path.Combine(copied,"bin",Path.GetFileName(file)));
    await File.WriteAllTextAsync(Path.Combine(copied,"bin","unexpected.dll"), "inert test data, not an executable");
    await Reject(() => new FileCryptoService(new GnuPgRuntime(copied)).EncryptAsync(source, At("injected.gpg"), password));
    Expect(!File.Exists(At("injected.gpg")), "En motor med extra DLL accepterades.");
});
Add("Password generator uses 64 distinct symbols and 24 draws", () =>
{
    Expect(PasswordGenerator.Alphabet.Length == 64 && PasswordGenerator.Alphabet.Distinct().Count() == 64, "Generatorns alfabet har ändrats.");
    var first = PasswordGenerator.Generate(); var second = PasswordGenerator.Generate();
    Expect(first.Length == 24 && first.All(c => PasswordGenerator.Alphabet.Contains(c)), "Generatorns utdata har fel format.");
    Expect(!first.SequenceEqual(second), "Generatorn gav samma lösenord.");
    Array.Clear(first); Array.Clear(second); return Task.CompletedTask;
});

var failed = 0;
try
{
    foreach (var (name, test) in tests)
    {
        try { await test(); Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.Error.WriteLine($"FAIL {name}: {e.GetType().Name}: {e.Message}"); }
    }
}
finally
{
    Array.Clear(password);
    var resolved = Path.GetFullPath(scratch);
    var intendedRoot = Path.GetFullPath(Path.Combine(root,"artifacts")) + Path.DirectorySeparatorChar;
    if (!resolved.StartsWith(intendedRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException("Testmappens identitet ändrades.");
    FileCryptoPaths.AssertNoReparsePoints(resolved);
    Directory.Delete(resolved, recursive: true); // Only this uniquely created test directory.
}
Console.WriteLine($"{tests.Count-failed}/{tests.Count} real file encryption tests passed. No user files or disks were changed.");
return failed == 0 ? 0 : 1;

async Task<byte[]> ExternalGpg(string input, params string[] options)
{
    // Windows GnuPG sockets have a short pathname limit, independent of .NET long paths.
    var home = At("g" + ++externalHomeNumber); Directory.CreateDirectory(home);
    var start = new ProcessStartInfo(runtime.GpgPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = runtime.BinPath };
    foreach (var arg in new[] { "--no-options", "--homedir", home, "--agent-program", runtime.AgentPath, "--batch", "--pinentry-mode", "loopback", "--passphrase-fd", "0", "--no-symkey-cache", "--output", "-" }.Concat(options).Concat(["--", input])) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    using var output = new MemoryStream();
    var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
    var errors = process.StandardError.ReadToEndAsync();
    var secret = Encoding.UTF8.GetBytes(password.Concat(['\n']).ToArray());
    try
    {
        await process.StandardInput.BaseStream.WriteAsync(secret); process.StandardInput.Close();
        await Task.WhenAll(copy, process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        var diagnostics = await errors;
        Expect(process.ExitCode == 0, "Extern GnuPG misslyckades: " + diagnostics);
        return output.ToArray();
    }
    finally
    {
        CryptographicOperations.ZeroMemory(secret);
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        var stop = new ProcessStartInfo(runtime.GpgConfPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--homedir", home, "--kill", "gpg-agent" }) stop.ArgumentList.Add(arg);
        using var cleanup = Process.Start(stop)!; await cleanup.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}

sealed class InlineProgress(Action<FileCryptoProgress> callback) : IProgress<FileCryptoProgress>
{
    public void Report(FileCryptoProgress value) => callback(value);
}
