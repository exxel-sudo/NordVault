using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace NordVault.Core;

/// <summary>
/// Windows Hello (fingerprint, face or PIN) through the Microsoft Passport key storage provider. A non-exportable RSA key,
/// held by Windows Hello and the TPM where present, decrypts a random AES key that seals the vault key. Every decrypt shows
/// the Hello prompt; another program cannot answer it. The capsule only works for this Windows user on this computer.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsHello
{
    private const string Provider = "Microsoft Passport Key Storage Provider";
    private const int PadPkcs1 = 2, AllowDecrypt = 1, OverwriteKey = 0x80;

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptOpenStorageProvider(out IntPtr provider, string name, int flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptCreatePersistedKey(IntPtr provider, out IntPtr key, string algorithm, string name, int legacySpec, int flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptOpenKey(IntPtr provider, out IntPtr key, string name, int legacySpec, int flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptSetProperty(IntPtr handle, string property, byte[] value, int size, int flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptFinalizeKey(IntPtr key, int flags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)] private static extern int NCryptExportKey(IntPtr key, IntPtr exportKey, string blobType, IntPtr parameters, byte[]? output, int outputSize, out int resultSize, int flags);
    [DllImport("ncrypt.dll")] private static extern int NCryptDecrypt(IntPtr key, byte[] input, int inputSize, IntPtr padding, byte[]? output, int outputSize, out int resultSize, int flags);
    [DllImport("ncrypt.dll")] private static extern int NCryptDeleteKey(IntPtr key, int flags);
    [DllImport("ncrypt.dll")] private static extern int NCryptFreeObject(IntPtr handle);

    private sealed record Capsule(int Version, string VaultId, string KeyName, string WrappedAesKey, string Nonce, string Tag, string Ciphertext);

    /// <summary>One capsule per Windows user and computer, so a vault on a USB stick can have Hello on several PCs.</summary>
    public static string CapsulePath(string root)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|" + WindowsIdentity.GetCurrent().User!.Value)))[..16];
        return Path.Combine(VaultKeyring.KeysFolder(root), "hello-" + id + ".json");
    }
    public static bool IsEnabled(string root) => File.Exists(CapsulePath(root));

    /// <summary>Whether the provider exists. Hello must also be set up (Settings → Accounts → Sign-in options).</summary>
    public static bool IsAvailable()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10)) return false;
        try { if (NCryptOpenStorageProvider(out var provider, Provider, 0) != 0) return false; NCryptFreeObject(provider); return true; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    private static string KeyName(string vaultId) => WindowsIdentity.GetCurrent().User!.Value + "//NordVault//Vault-" + vaultId;

    /// <summary>Creates the Hello key (shows the Hello prompt) and writes the capsule.</summary>
    internal static void Enable(string root, string vaultId, ReadOnlySpan<char> vaultKey, IntPtr window, string message)
    {
        Check(NCryptOpenStorageProvider(out var provider, Provider, 0), "Windows Hello finns inte på den här datorn.");
        IntPtr key = IntPtr.Zero; var name = KeyName(vaultId);
        try
        {
            Check(NCryptCreatePersistedKey(provider, out key, "RSA", name, 0, OverwriteKey), "Windows Hello-nyckeln kunde inte skapas. Är Windows Hello (PIN) inställt för kontot, och används datorn direkt och inte via fjärrskrivbord?");
            SetInt(key, "Length", 2048); SetInt(key, "Key Usage", AllowDecrypt);
            // Ask for the gesture every time (property name differs between Windows versions; ignore if unsupported).
            if (NCryptSetProperty(key, "NgcCacheType", BitConverter.GetBytes(1), 4, 0) != 0) NCryptSetProperty(key, "NgcCacheTypeProperty", BitConverter.GetBytes(1), 4, 0);
            SetUi(key, window, message);
            Check(NCryptFinalizeKey(key, 0), "Windows Hello avbröts eller är inte inställt.");
            Check(NCryptExportKey(key, IntPtr.Zero, "RSAPUBLICBLOB", IntPtr.Zero, null, 0, out var size, 0), "Windows Hello-nyckeln kunde inte läsas.");
            var blob = new byte[size]; Check(NCryptExportKey(key, IntPtr.Zero, "RSAPUBLICBLOB", IntPtr.Zero, blob, size, out size, 0), "Windows Hello-nyckeln kunde inte läsas.");
            using var rsa = RSA.Create(); rsa.ImportParameters(PublicParameters(blob));
            var aesKey = RandomNumberGenerator.GetBytes(32); var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16];
            var plain = Encoding.UTF8.GetBytes(vaultKey.ToArray()); var cipher = new byte[plain.Length];
            try
            {
                using (var aes = new AesGcm(aesKey, 16)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.ASCII.GetBytes(vaultId));
                var capsule = new Capsule(1, vaultId, name, Convert.ToBase64String(rsa.Encrypt(aesKey, RSAEncryptionPadding.Pkcs1)), Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(cipher));
                var path = CapsulePath(root); Directory.CreateDirectory(Path.GetDirectoryName(path)!); FileCryptoPaths.AssertNoReparsePoints(path);
                File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(capsule));
            }
            finally { CryptographicOperations.ZeroMemory(aesKey); CryptographicOperations.ZeroMemory(plain); }
        }
        catch { if (key != IntPtr.Zero) { NCryptDeleteKey(key, 0); key = IntPtr.Zero; } throw; }
        finally { if (key != IntPtr.Zero) NCryptFreeObject(key); NCryptFreeObject(provider); }
    }

    /// <summary>Shows the Hello prompt and returns the vault key. Caller clears it.</summary>
    internal static (char[] Key, string VaultId) Unlock(string root, IntPtr window, string message)
    {
        var path = CapsulePath(root);
        if (!File.Exists(path) || new FileInfo(path).Length > 16384) throw new IOException("Windows Hello är inte påslaget för valvet på den här datorn.");
        var capsule = JsonSerializer.Deserialize<Capsule>(File.ReadAllBytes(path)) ?? throw new IOException("Windows Hello-kapseln är skadad.");
        if (capsule.Version != 1 || !Guid.TryParseExact(capsule.VaultId, "N", out _) || capsule.KeyName != KeyName(capsule.VaultId)) throw new IOException("Windows Hello-kapseln är skadad.");
        Check(NCryptOpenStorageProvider(out var provider, Provider, 0), "Windows Hello finns inte på den här datorn.");
        IntPtr key = IntPtr.Zero; byte[]? aesKey = null; byte[]? plain = null;
        try
        {
            Check(NCryptOpenKey(provider, out key, capsule.KeyName, 0, 0), "Windows Hello-nyckeln finns inte längre. Lås upp med huvudlösenordet och slå på Windows Hello igen.");
            SetUi(key, window, message);
            var wrapped = Convert.FromBase64String(capsule.WrappedAesKey);
            Check(NCryptDecrypt(key, wrapped, wrapped.Length, IntPtr.Zero, null, 0, out var size, PadPkcs1), "Windows Hello avbröts.");
            var output = new byte[size];
            Check(NCryptDecrypt(key, wrapped, wrapped.Length, IntPtr.Zero, output, size, out size, PadPkcs1), "Windows Hello avbröts.");
            aesKey = output[..size]; CryptographicOperations.ZeroMemory(output);
            var cipher = Convert.FromBase64String(capsule.Ciphertext); plain = new byte[cipher.Length];
            using (var aes = new AesGcm(aesKey, 16)) aes.Decrypt(Convert.FromBase64String(capsule.Nonce), cipher, Convert.FromBase64String(capsule.Tag), plain, Encoding.ASCII.GetBytes(capsule.VaultId));
            return (Encoding.UTF8.GetChars(plain), capsule.VaultId);
        }
        catch (Exception error) when (error is CryptographicException or FormatException) { throw new IOException("Windows Hello-kapseln är skadad eller hör till ett annat valv."); }
        finally
        {
            if (aesKey is not null) CryptographicOperations.ZeroMemory(aesKey); if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            if (key != IntPtr.Zero) NCryptFreeObject(key); NCryptFreeObject(provider);
        }
    }

    /// <summary>Removes the capsule and the Hello key. Never fails because the key is already gone.</summary>
    internal static void Disable(string root, string vaultId)
    {
        var path = CapsulePath(root);
        if (File.Exists(path)) File.Delete(path);
        try
        {
            if (NCryptOpenStorageProvider(out var provider, Provider, 0) != 0) return;
            try { if (NCryptOpenKey(provider, out var key, KeyName(vaultId), 0, 0) == 0 && NCryptDeleteKey(key, 0) != 0) NCryptFreeObject(key); }
            finally { NCryptFreeObject(provider); }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }

    private static void SetUi(IntPtr key, IntPtr window, string message)
    {
        if (window != IntPtr.Zero) NCryptSetProperty(key, "HWND Handle", BitConverter.GetBytes(window.ToInt64()), IntPtr.Size, 0);
        var text = Encoding.Unicode.GetBytes(message + "\0"); NCryptSetProperty(key, "Use Context", text, text.Length, 0);
    }
    private static void SetInt(IntPtr key, string property, int value) => Check(NCryptSetProperty(key, property, BitConverter.GetBytes(value), 4, 0), "Windows Hello-nyckeln kunde inte skapas.");
    private static void Check(int status, string message) { if (status != 0) throw new IOException(message + $" (kod 0x{status:X8})"); }

    /// <summary>BCRYPT_RSAKEY_BLOB: magic, bit length, exponent size, modulus size, two prime sizes; then exponent and modulus, big-endian.</summary>
    private static RSAParameters PublicParameters(byte[] blob)
    {
        var exponent = BitConverter.ToInt32(blob, 8); var modulus = BitConverter.ToInt32(blob, 12);
        return new RSAParameters { Exponent = blob[24..(24 + exponent)], Modulus = blob[(24 + exponent)..(24 + exponent + modulus)] };
    }
}
