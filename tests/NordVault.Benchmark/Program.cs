using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NordVault.Core;

var root=Directory.GetCurrentDirectory();
using var work=new PrivateWorkspace(Path.Combine(root,"artifacts"));
var crypto=new FileCryptoService(new GnuPgRuntime(Path.Combine(root,"tools","gnupg")));
var password="Benchmark-test-only-password".ToCharArray();
try
{
    const int total=64*1024*1024;
    var source=Path.Combine(work.Path,"random64MiB.bin");
    using(var file=new FileStream(source,FileMode.CreateNew,FileAccess.Write,FileShare.None))
    {var block=new byte[1024*1024];for(var i=0;i<64;i++){RandomNumberGenerator.Fill(block);await file.WriteAsync(block);}file.Flush(true);Array.Clear(block);}
    using(var warmup=new SensitiveBytes(new byte[128]))await crypto.EncryptMetadataAsync(warmup.Memory,Path.Combine(work.Path,"warmup.gpg"),password);
    var timer=Stopwatch.StartNew();await crypto.EncryptAsync(source,Path.Combine(work.Path,"cipher.gpg"),password);timer.Stop();var encrypt=timer.Elapsed.TotalSeconds;
    timer.Restart();await crypto.DecryptAsync(Path.Combine(work.Path,"cipher.gpg"),Path.Combine(work.Path,"restored.bin"),password);timer.Stop();var decrypt=timer.Elapsed.TotalSeconds;
    using var first=File.OpenRead(source);using var second=File.OpenRead(Path.Combine(work.Path,"restored.bin"));
    var originalHash=await SHA256.HashDataAsync(first);var restoredHash=await SHA256.HashDataAsync(second);
    if(!CryptographicOperations.FixedTimeEquals(originalHash,restoredHash))throw new IOException("Benchmark roundtrip failed.");
    timer.Restart();await crypto.EncryptMetadataAsync("small metadata test"u8.ToArray(),Path.Combine(work.Path,"small.gpg"),password);timer.Stop();var small=timer.Elapsed.TotalSeconds;
    var result=new{Product="NordVault",Version="0.3.0",MeasuredUtc=DateTimeOffset.UtcNow,Bytes=total,EncryptSeconds=encrypt,DecryptSeconds=decrypt,EncryptMiBPerSecond=64/encrypt,DecryptMiBPerSecond=64/decrypt,SmallEncryptSeconds=small,VerifiedRoundtrip=true,Windows=Environment.OSVersion.VersionString,ProcessorCount=Environment.ProcessorCount,StoragePath=root,Notes="One warm-run sample. Includes engine verification/startup, password S2K, flush, and full two-pass authenticated decryption. Cached I/O; not a raw disk or BitLocker benchmark."};
    var json=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true});await File.WriteAllTextAsync(Path.Combine(root,"artifacts","performance-0.3.0.json"),json);Console.WriteLine(json);return 0;
}
finally{Array.Clear(password);}
