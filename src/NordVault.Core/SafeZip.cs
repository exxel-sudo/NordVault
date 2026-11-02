using System.IO.Compression;

namespace NordVault.Core;

public static class SafeZip
{
    public static string Relative(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Contains('\\') || name.StartsWith('/') ||
            name.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 || name.Any(char.IsControl) ||
            name.Split('/').Any(part => !VaultCatalog.IsSafeName(part))) throw new IOException("En osäker arkivsökväg avvisades.");
        return name;
    }
    public static Dictionary<string, ZipArchiveEntry> Index(ZipArchive archive, int maximumEntries = 6000)
    {
        if (archive.Entries.Count > maximumEntries) throw new IOException("Arkivet innehåller för många objekt.");
        var result = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = Relative(entry.FullName);
            if (!result.TryAdd(name, entry)) throw new IOException("Arkivet har dubbla objekt.");
            // Unix symlinks must never be interpreted as files or followed.
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new IOException("Arkivlänkar tillåts inte.");
        }
        return result;
    }
    public static async Task CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken cancellation = default)
    {
        var buffer = new byte[65536]; long bytes = 0;
        try
        {
            int read;
            while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
            { bytes = checked(bytes + read); if (bytes > maximum) throw new IOException("Arkivobjektet är för stort."); await output.WriteAsync(buffer.AsMemory(0, read), cancellation); }
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer); }
    }
}
