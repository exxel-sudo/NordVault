using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace NordVault.Core;

/// <summary>A login saved from Firefox while the vault was locked. Clear the password with <see cref="Dispose"/>.</summary>
public sealed class InboxLogin(string file, string url, string username, char[] password, DateTimeOffset saved) : IDisposable
{
    public string File { get; } = file;
    public string Url { get; } = url;
    public string Username { get; } = username;
    public ReadOnlyMemory<char> Password => password;
    public DateTimeOffset Saved { get; } = saved;
    public void Dispose() => Array.Clear(password);
}

public sealed record InboxContents(IReadOnlyList<InboxLogin> Logins, IReadOnlyList<string> Unreadable);

/// <summary>
/// "Brevlådan": Firefox can save a login while the vault is locked. Each login is encrypted by GnuPG to the vault's inbox public key
/// (inbox.pub) and lands in inbox\. Only the secret key, kept encrypted under the vault key (inbox-key.gpg), can open them, so saving
/// needs no password and reveals nothing. Anyone who can write to the folder could add entries, so the user approves them on unlock.
/// </summary>
[SupportedOSPlatform("windows")]
public static class LoginInbox
{
    public const int MaximumEntries = 200;
    internal const int MaximumEntryBytes = 16 * 1024;
    internal const int MaximumKeyBytes = 16 * 1024;
    public static string PublicKeyPath(string root) => Path.Combine(root, "inbox.pub");
    internal static string SecretKeyPath(string root) => Path.Combine(root, "inbox-key.gpg");
    public static string Folder(string root) => Path.Combine(root, "inbox");

    /// <summary>The public key in the vault folder, or null when this vault has no inbox yet (it is made on the next unlock).</summary>
    public static byte[]? ReadPublicKey(string root)
    {
        var path = PublicKeyPath(root);
        try
        {
            FileCryptoPaths.AssertNoReparsePoints(path);
            if (!System.IO.File.Exists(path) || new FileInfo(path).Length is 0 or > MaximumKeyBytes || !System.IO.File.Exists(SecretKeyPath(root))) return null;
            return System.IO.File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    public static string[] Files(string root)
    {
        var folder = Folder(root);
        if (!Directory.Exists(folder)) return [];
        FileCryptoPaths.AssertNoReparsePoints(folder);
        return Directory.EnumerateFiles(folder, "*.gpg").Where(f => Path.GetFileNameWithoutExtension(f) is { Length: 32 } name && name.All(Uri.IsHexDigit))
            .OrderBy(f => System.IO.File.GetLastWriteTimeUtc(f)).ToArray();
    }

    /// <summary>Saves one login, encrypted to the public key. The vault stays locked and no master password is asked for.</summary>
    public static async Task AddAsync(string root, GnuPgRuntime runtime, ReadOnlyMemory<byte> publicKey, Uri page, string username, ReadOnlyMemory<char> password, CancellationToken cancellation = default)
    {
        if (password.IsEmpty || password.Length > 1024) throw new ArgumentException("Lösenordet saknas eller är för långt.");
        if (username.Length > 512) throw new ArgumentException("Användarnamnet är för långt.");
        if (Files(root).Length >= MaximumEntries) throw new IOException("Brevlådan är full. Lås upp valvet så att inloggningarna läggs in.");
        var folder = Folder(root);
        Directory.CreateDirectory(folder); FileCryptoPaths.AssertNoReparsePoints(folder);
        using var buffer = new MemoryStream();
        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("v", 1);
                writer.WriteString("url", page.GetLeftPart(UriPartial.Authority));
                writer.WriteString("username", username.Trim());
                writer.WriteString("password", password.Span);
                writer.WriteString("saved", DateTimeOffset.UtcNow);
                writer.WriteEndObject();
            }
            if (buffer.Length > MaximumEntryBytes) throw new IOException("Inloggningen är för stor.");
            buffer.TryGetBuffer(out var content);
            var file = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".gpg");
            await new FileCryptoService(runtime).EncryptToKeyAsync(content, publicKey, file, cancellation);
            PortableCipherAccess.Inherit(file);
        }
        finally { if (buffer.TryGetBuffer(out var used)) CryptographicOperations.ZeroMemory(used.AsSpan()); }
    }

    internal static InboxLogin? Parse(string file, ReadOnlySpan<byte> json)
    {
        string? url = null, username = null; char[]? password = null; DateTimeOffset saved = default; var version = 0;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 2 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString(); if (!reader.Read()) return null;
                switch (name)
                {
                    case "v" when reader.TokenType == JsonTokenType.Number: version = reader.GetInt32(); break;
                    case "url" when reader.TokenType == JsonTokenType.String: url = reader.GetString(); break;
                    case "username" when reader.TokenType == JsonTokenType.String: username = reader.GetString(); break;
                    case "saved" when reader.TokenType == JsonTokenType.String: saved = reader.GetDateTimeOffset(); break;
                    case "password" when reader.TokenType == JsonTokenType.String && password is null:
                        var chars = new char[reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length];
                        var count = reader.CopyString(chars);
                        password = chars[..count]; Array.Clear(chars);
                        break;
                    default: reader.Skip(); break;
                }
            }
            if (version != 1 || url is null || username is not { Length: <= 512 } || password is not { Length: > 0 and <= 1024 } ||
                VaultItemMatcher.Parse(url) is not { } page || (page.Scheme != "https" && !VaultItemMatcher.IsLocalHost(page.IdnHost)))
            { if (password is not null) Array.Clear(password); return null; }
            return new InboxLogin(file, url, username, password, saved);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or ArgumentException)
        { if (password is not null) Array.Clear(password); return null; }
    }
}
