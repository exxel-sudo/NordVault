using System.Text.RegularExpressions;

namespace NordVault.Core;

/// <summary>
/// Pasted text to links: one or more web addresses per line, optionally with a title on the same line
/// ("Banken - https://bank.se", "https://bank.se Banken") or bare domains ("exempel.se"). Duplicates in the paste are dropped.
/// </summary>
public static class LinkParser
{
    public const int MaximumLinks = 5000;
    private static readonly Regex Url = new(@"https?://[^\s<>""'`]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    private static readonly Regex BareDomain = new(@"^(?:www\.)?[\p{L}\p{N}-]+(?:\.[\p{L}\p{N}-]+)*\.[\p{L}]{2,}(?:[/?#]\S*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    public static List<ImportedBookmark> Parse(string text, string note = "", bool isPrivate = false)
    {
        var result = new List<ImportedBookmark>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var urls = Url.Matches(line).Select(m => m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '>')).ToList();
            var title = line;
            foreach (var url in urls) title = title.Replace(url, " ");
            if (urls.Count == 0 && BareDomain.IsMatch(line)) { urls.Add("https://" + line); title = ""; }
            title = title.Trim(' ', '\t', '-', '–', '—', '|', ':', ',', ';', '(', ')', '[', ']', '<', '>', '"', '\'', '*', '•');
            foreach (var url in urls)
            {
                if (url.Length > 2048 || VaultItemMatcher.Parse(url) is not { } uri || !seen.Add(uri.AbsoluteUri.TrimEnd('/'))) continue;
                var name = urls.Count == 1 && title.Length > 0 ? title : uri.IdnHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.IdnHost[4..] : uri.IdnHost;
                name = new string(name.Where(c => !char.IsControl(c)).ToArray());
                if (name.Length > 200) name = name[..200];
                result.Add(new ImportedBookmark(name, uri.AbsoluteUri, "", note, isPrivate));
                if (result.Count >= MaximumLinks) return result;
            }
        }
        return result;
    }
}
