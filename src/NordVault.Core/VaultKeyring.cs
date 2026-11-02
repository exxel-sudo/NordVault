using System.Buffers;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace NordVault.Core;

/// <summary>
/// Key capsules in &lt;vault&gt;\keys. Vault data is encrypted with the vault key; each capsule holds that key, encrypted with
/// GnuPG's authenticated format under one way of unlocking: the master password (password.gpg) or a one-time recovery code
/// (recovery-*.gpg). Windows Hello capsules are in <see cref="WindowsHello"/>.
/// Vaults from 0.3.x keep their master password as vault key, so their files need no re-encryption and older versions still open them.
/// </summary>
[SupportedOSPlatform("windows")]
public static class VaultKeyring
{
    public const string RecoveryAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 32 symbols: 5 bits each, no 0/O, 1/I
    public const int RecoveryCodeSymbols = 40;                                 // 200 bits
    public const int MaximumRecoveryCodes = 10;

    public static string KeysFolder(string root) => Path.Combine(root, "keys");
    public static string PasswordCapsule(string root) => Path.Combine(KeysFolder(root), "password.gpg");
    public static string[] RecoveryCapsules(string root) =>
        Directory.Exists(KeysFolder(root)) ? Directory.GetFiles(KeysFolder(root), "recovery-*.gpg").Where(p => Guid.TryParseExact(Path.GetFileNameWithoutExtension(p)[9..], "N", out _)).Take(MaximumRecoveryCodes).ToArray() : [];

    /// <summary>New vaults: 64 random hex characters (256 bits).</summary>
    internal static char[] NewVaultKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToCharArray();

    internal static byte[] Encode(string vaultId, ReadOnlySpan<char> key)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        { writer.WriteStartObject(); writer.WriteNumber("Version", 1); writer.WriteString("VaultId", vaultId); writer.WriteString("Key", key); writer.WriteEndObject(); }
        var result = buffer.WrittenSpan.ToArray(); buffer.Clear();
        return result;
    }

    /// <summary>Caller clears the key.</summary>
    internal static (char[] Key, string VaultId) Decode(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json); char[]? key = null; string? vaultId = null; var version = 0;
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw Invalid();
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString(); if (!reader.Read()) throw Invalid();
                if (name == "Key" && reader.TokenType == JsonTokenType.String)
                {
                    var scratch = new char[reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length];
                    var length = reader.CopyString(scratch); key = scratch[..length]; Array.Clear(scratch);
                }
                else if (name == "VaultId" && reader.TokenType == JsonTokenType.String) vaultId = reader.GetString();
                else if (name == "Version" && reader.TokenType == JsonTokenType.Number) version = reader.GetInt32();
                else reader.Skip();
            }
            if (version != 1 || key is not { Length: >= 1 and <= 256 } || vaultId is null || !Guid.TryParseExact(vaultId, "N", out _)) throw Invalid();
            return (key, vaultId);
        }
        catch { if (key is not null) Array.Clear(key); throw; }
    }

    internal static async Task WriteAsync(FileCryptoService crypto, string path, string vaultId, ReadOnlyMemory<char> key, ReadOnlyMemory<char> passphrase, CancellationToken cancellation)
    {
        var folder = Path.GetDirectoryName(path)!; Directory.CreateDirectory(folder); FileCryptoPaths.AssertNoReparsePoints(folder);
        var temporary = Path.Combine(folder, "capsule-" + Guid.NewGuid().ToString("N") + ".tmp");
        var bytes = Encode(vaultId, key.Span);
        try
        {
            await crypto.EncryptMetadataAsync(bytes, temporary, passphrase, cancellation);
            PortableCipherAccess.Inherit(temporary);
            FileCryptoPaths.AssertNoReparsePoints(path);
            File.Move(temporary, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Caller clears the key. Throws when the passphrase is wrong or the capsule is damaged.</summary>
    internal static async Task<(char[] Key, string VaultId)> ReadAsync(FileCryptoService crypto, string path, ReadOnlyMemory<char> passphrase, CancellationToken cancellation)
    {
        using var plaintext = await crypto.DecryptMetadataAsync(path, passphrase, cancellation);
        return Decode(plaintext.Memory.Span);
    }

    /// <summary>40 symbols shown as 8 groups of 5. Caller clears the result.</summary>
    public static char[] NewRecoveryCode()
    {
        var code = new char[RecoveryCodeSymbols];
        for (var i = 0; i < code.Length; i++) code[i] = RecoveryAlphabet[RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)];
        return code;
    }
    public static string Format(ReadOnlySpan<char> code)
    {
        var groups = new List<string>();
        for (var i = 0; i < code.Length; i += 5) groups.Add(new string(code.Slice(i, Math.Min(5, code.Length - i))));
        return string.Join("-", groups);
    }
    /// <summary>Accepts the code with or without dashes and spaces, in any case. 0, 1, O and I are not used in codes.</summary>
    public static char[] Normalize(ReadOnlySpan<char> typed)
    {
        var result = new char[RecoveryCodeSymbols]; var n = 0;
        foreach (var raw in typed)
        {
            if (raw is '-' or ' ' or '\t') continue;
            var c = char.ToUpperInvariant(raw);
            if (RecoveryAlphabet.IndexOf(c) < 0 || n == result.Length) { Array.Clear(result); throw new ArgumentException("Koden ska vara 40 tecken (8 grupper om 5) med bokstäver och siffror 2–9."); }
            result[n++] = c;
        }
        if (n != result.Length) { Array.Clear(result); throw new ArgumentException("Koden ska vara 40 tecken (8 grupper om 5)."); }
        return result;
    }

    private static IOException Invalid() => new("Valvets nyckelkapsel är ogiltig.");
}
