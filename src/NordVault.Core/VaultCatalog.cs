using System.Text.Json;
using System.Text.RegularExpressions;

namespace NordVault.Core;

public sealed record VaultEntry(string Id, string Name, long Size, string CipherSha256, DateTimeOffset AddedUtc)
{
    public string SizeText => Size < 1048576 ? $"{Size / 1024.0:0.0} KiB" : $"{Size / 1048576.0:0.0} MiB";
    public string AddedText => AddedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
public sealed record VaultCatalog(int Version, string VaultId, VaultEntry[] Entries)
{
    public static VaultCatalog Empty() => new(1, Guid.NewGuid().ToString("N"), []);
    /// <summary>Archives may hold folders: names are then relative paths with '/' between safe names.</summary>
    public static VaultCatalog Read(ReadOnlySpan<byte> bytes, bool allowFolders = false)
    {
        var catalog = JsonSerializer.Deserialize<VaultCatalog>(bytes) ?? throw new IOException("Innehållsförteckningen saknas.");
        if (catalog.Version != 1 || !Guid.TryParseExact(catalog.VaultId, "N", out _) || catalog.Entries is null || catalog.Entries.Length > 5000)
            throw new IOException("Valvets format stöds inte.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in catalog.Entries)
        {
            if (entry is null || !Guid.TryParseExact(entry.Id, "N", out _) || !ids.Add(entry.Id) ||
                entry.Size is < 0 or > CryptoLimits.MaximumFileBytes || !(allowFolders ? IsSafeRelativeName(entry.Name) : IsSafeName(entry.Name)) ||
                entry.CipherSha256 is null || !Regex.IsMatch(entry.CipherSha256, "\\A[0-9A-F]{64}\\z", RegexOptions.CultureInvariant))
                throw new IOException("Valvets innehållsförteckning är ogiltig.");
        }
        return catalog;
    }
    public static bool IsSafeRelativeName(string? name) => name is { Length: > 0 and <= 400 } && name.Split('/') is { Length: <= 32 } parts && parts.All(IsSafeName);
    public static bool IsSafeName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 200 &&
        name is not ("." or "..") && !name.Any(char.IsControl) && name.IndexOfAny(['\\','/',':','*','?','"','<','>','|']) < 0 &&
        !name.EndsWith('.') && !name.EndsWith(' ') &&
        !Regex.IsMatch(name, "\\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\..*)?\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
