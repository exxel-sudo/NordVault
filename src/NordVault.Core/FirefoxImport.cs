using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NordVault.Core;

/// <summary>A login from Firefox's password export. Clear <see cref="Password"/> after use.</summary>
public sealed record ImportedLogin(string Url, string Username, char[] Password);
/// <param name="Note">Text for the link's note; when empty, the Firefox folder is used.</param>
public sealed record ImportedBookmark(string Title, string Url, string Folder, string Note = "", bool Private = false);
/// <param name="Flagged">Logins marked "Att byta" because they were stored in the browser.</param>
public sealed record ImportSummary(int Created, int Updated, int Skipped, int Flagged = 0);

/// <summary>Reads files exported from Firefox: logins (about:logins → Exportera lösenord, CSV) and bookmarks (HTML or JSON backup).</summary>
public static class FirefoxImport
{
    public const long MaximumFileBytes = 64L * 1024 * 1024;

    public enum FileKind { Unknown, Logins, BookmarksHtml, BookmarksJson }

    private static readonly Regex SecretParameter = new("(?:^|[?&#;])(?:[a-z_]*token|access_token|id_token|key|apikey|api_key|password|passwd|pwd|pass|secret|session|sessionid|sid|sig|signature|code|auth|otp|reset|ticket|jwt)=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Links that carry tokens, keys, reset codes or sessions work like passwords.</summary>
    public static bool HasSecret(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var tail = uri.Query + uri.Fragment;
        return SecretParameter.IsMatch(tail) || uri.AbsolutePath.Contains("reset", StringComparison.OrdinalIgnoreCase) && tail.Length > 1;
    }
    /// <summary>Keeps scheme, host and path; drops the query and fragment that held the secret.</summary>
    public static ImportedBookmark WithoutSecret(ImportedBookmark bookmark)
    {
        var uri = new Uri(bookmark.Url);
        return bookmark with { Url = uri.GetLeftPart(UriPartial.Path) };
    }

    public static byte[] ReadLimited(string path)
    {
        path = FileCryptoPaths.Validate(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumFileBytes) throw new IOException("Filen är för stor för import (högst 64 MiB).");
        var bytes = new byte[input.Length]; input.ReadExactly(bytes);
        return bytes;
    }

    public static FileKind Detect(ReadOnlySpan<byte> bytes)
    {
        var head = Encoding.UTF8.GetString(bytes[..Math.Min(bytes.Length, 4096)]).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (head.StartsWith("<!DOCTYPE NETSCAPE-Bookmark-file-1", StringComparison.OrdinalIgnoreCase) || head.Contains("<DT><A", StringComparison.OrdinalIgnoreCase)) return FileKind.BookmarksHtml;
        if (head.StartsWith('{') && head.Contains("\"children\"", StringComparison.Ordinal)) return FileKind.BookmarksJson;
        var firstLine = head.Split('\n')[0].ToLowerInvariant();
        if (firstLine.Contains("url") && firstLine.Contains("username") && firstLine.Contains("password")) return FileKind.Logins;
        return FileKind.Unknown;
    }

    /// <summary>Firefox CSV: "url","username","password",... Only http/https logins are kept. Caller clears the passwords.</summary>
    public static List<ImportedLogin> ParseLogins(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[Encoding.UTF8.GetCharCount(bytes)];
        var result = new List<ImportedLogin>();
        try
        {
            Encoding.UTF8.GetChars(bytes, chars);
            var rows = CsvRows(chars);
            if (rows.Count == 0) throw new IOException("Lösenordsfilen är tom.");
            var header = rows[0].Select(f => new string(f).Trim().TrimStart('\uFEFF').ToLowerInvariant()).ToArray();
            int url = Array.IndexOf(header, "url"), user = Array.IndexOf(header, "username"), pass = Array.IndexOf(header, "password");
            foreach (var field in rows[0]) Array.Clear(field);
            if (url < 0 || user < 0 || pass < 0) throw new IOException("Filen saknar kolumnerna url, username och password. Exportera från about:logins i Firefox.");
            foreach (var row in rows.Skip(1))
            {
                try
                {
                    if (row.Count <= Math.Max(url, Math.Max(user, pass))) continue;
                    var site = new string(row[url]).Trim();
                    if (VaultItemMatcher.Parse(site) is null || !site.Contains("://") || row[pass].Length == 0 || row[pass].Length > VaultItemStore.MaximumPasswordChars) continue;
                    result.Add(new ImportedLogin(site, new string(row[user]).Trim(), (char[])row[pass].Clone()));
                }
                finally { foreach (var field in row) Array.Clear(field); }
            }
            return result;
        }
        catch { foreach (var login in result) Array.Clear(login.Password); throw; }
        finally { Array.Clear(chars); }
    }

    /// <summary>RFC 4180: quoted fields, doubled quotes, CR/LF inside quotes. Fields are char[] so passwords never become strings here.</summary>
    private static List<List<char[]>> CsvRows(ReadOnlySpan<char> text)
    {
        var rows = new List<List<char[]>>(); var row = new List<char[]>(); var field = new StringBuilderSecure();
        var quoted = false; var i = 0; var any = false;
        while (i < text.Length)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; } quoted = false; }
                else field.Append(c);
                i++; continue;
            }
            switch (c)
            {
                case '"': quoted = true; any = true; break;
                case ',': row.Add(field.Take()); any = true; break;
                case '\r': break;
                case '\n': if (any || field.Length > 0) { row.Add(field.Take()); rows.Add(row); } row = []; any = false; break;
                default: field.Append(c); any = true; break;
            }
            i++;
        }
        if (any || field.Length > 0) { row.Add(field.Take()); rows.Add(row); }
        field.Clear();
        return rows;
    }

    private sealed class StringBuilderSecure
    {
        private char[] _buffer = new char[64]; public int Length { get; private set; }
        public void Append(char c)
        {
            if (Length == _buffer.Length) { var next = new char[_buffer.Length * 2]; _buffer.AsSpan(0, Length).CopyTo(next); Array.Clear(_buffer); _buffer = next; }
            _buffer[Length++] = c;
        }
        public char[] Take() { var result = _buffer[..Length]; Array.Clear(_buffer, 0, Length); Length = 0; return result; }
        public void Clear() { Array.Clear(_buffer); Length = 0; }
    }

    /// <summary>Netscape bookmark HTML (Firefox → Bibliotek → Exportera bokmärken till HTML).</summary>
    public static List<ImportedBookmark> ParseBookmarksHtml(string html)
    {
        var result = new List<ImportedBookmark>(); var folders = new Stack<string>(); string? pendingFolder = null;
        var token = new Regex("<(?<close>/)?(?<tag>DL|H3|A)\\b(?<attrs>[^>]*)>(?:(?<text>[^<]*)</(?:H3|A)>)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(10));
        var href = new Regex("\\bHREF\\s*=\\s*\"(?<url>[^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (Match m in token.Matches(html))
        {
            var tag = m.Groups["tag"].Value.ToUpperInvariant(); var closing = m.Groups["close"].Success;
            if (tag == "H3" && !closing) pendingFolder = WebUtility.HtmlDecode(m.Groups["text"].Value).Trim();
            else if (tag == "DL" && !closing) { folders.Push(pendingFolder ?? ""); pendingFolder = null; }
            else if (tag == "DL" && closing) { if (folders.Count > 0) folders.Pop(); }
            else if (tag == "A" && !closing && href.Match(m.Groups["attrs"].Value) is { Success: true } link)
                Add(result, WebUtility.HtmlDecode(m.Groups["text"].Value), WebUtility.HtmlDecode(link.Groups["url"].Value), folders.Reverse());
        }
        return result;
    }

    /// <summary>Firefox bookmark backup (.json from Bibliotek → Säkerhetskopiera).</summary>
    public static List<ImportedBookmark> ParseBookmarksJson(ReadOnlySpan<byte> json)
    {
        var result = new List<ImportedBookmark>();
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 128 });
        void Walk(JsonElement node, List<string> path)
        {
            if (node.ValueKind != JsonValueKind.Object) return;
            var title = node.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            if (node.TryGetProperty("uri", out var uri) && uri.ValueKind == JsonValueKind.String) Add(result, title, uri.GetString() ?? "", path);
            if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                var next = title.Length > 0 && node.TryGetProperty("typeCode", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt32() == 2 ? [.. path, title] : path;
                foreach (var child in children.EnumerateArray()) Walk(child, next);
            }
        }
        Walk(document.RootElement, []);
        return result;
    }

    private static void Add(List<ImportedBookmark> result, string title, string url, IEnumerable<string> folders)
    {
        url = url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        if (url.Length > 2048 || VaultItemMatcher.Parse(url) is null) return;
        title = new string(title.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (title.Length == 0) title = VaultItemMatcher.Host(url) ?? url;
        if (title.Length > 200) title = title[..200];
        var folder = string.Join(" / ", folders.Where(f => f.Length > 0).Select(f => new string(f.Where(c => !char.IsControl(c)).ToArray())));
        if (folder.Length > 180) folder = folder[..180];
        result.Add(new ImportedBookmark(title, url, folder));
    }
}
