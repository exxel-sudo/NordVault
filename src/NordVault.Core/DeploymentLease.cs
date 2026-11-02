using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace NordVault.Core;

[SupportedOSPlatform("windows")]
internal sealed class DeploymentLease : IDisposable
{
    private readonly Semaphore _semaphore;
    public DeploymentLease(string folder)
    {
        var id=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).TrimEnd('\\').ToUpperInvariant())));
        _semaphore=new Semaphore(1,1,@"Local\NordVault.Install."+id);
        if(!_semaphore.WaitOne(0)){_semaphore.Dispose();throw new IOException("Ett annat installationsprogram arbetar redan med denna mapp. Vänta tills det är klart.");}
    }
    public void Dispose(){_semaphore.Release();_semaphore.Dispose();}
}
