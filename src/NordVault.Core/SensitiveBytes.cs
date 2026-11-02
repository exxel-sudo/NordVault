using System.Security.Cryptography;

namespace NordVault.Core;

public sealed class SensitiveBytes(byte[] bytes) : IDisposable
{
    public ReadOnlyMemory<byte> Memory => bytes;
    public void Dispose() => CryptographicOperations.ZeroMemory(bytes);
}
