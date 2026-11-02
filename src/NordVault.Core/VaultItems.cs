using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NordVault.Core;

public enum VaultItemKind { Login, Link, Note, Account }

/// <summary>Computed in the open session, never stored.</summary>
[Flags] public enum PasswordIssue { None = 0, Reused = 1, Weak = 2, Breached = 4 }

/// <summary>Visible item fields. The password is never part of this record; it stays sealed in <see cref="VaultItemStore"/>.</summary>
/// <param name="MustChange">Imported from a browser that may have been compromised: shown in "Att byta" until the password is changed here.</param>
public sealed record VaultItem(string Id, VaultItemKind Kind, string Title, string Url, string Username, string LinkedAccount, string Notes, bool HasPassword, DateTimeOffset CreatedUtc, DateTimeOffset ModifiedUtc,
    bool MustChange = false, DateTimeOffset? PasswordChangedUtc = null, DateTimeOffset? ImportedUtc = null, PasswordIssue Issue = PasswordIssue.None, bool Private = false)
{
    public bool NeedsAttention => MustChange || Issue != PasswordIssue.None;
    public (int Rank, string Label) ChangePriority => VaultItemRisk.Category(Host);
    public string ChangeText
    {
        get
        {
            if (!NeedsAttention) return "";
            var reasons = new List<string>();
            if (Issue.HasFlag(PasswordIssue.Breached)) reasons.Add("i känd läcka");
            if (MustChange) reasons.Add("kan ha läckt");
            if (Issue.HasFlag(PasswordIssue.Reused)) reasons.Add("återanvänt");
            if (Issue.HasFlag(PasswordIssue.Weak)) reasons.Add("svagt");
            return "⚠ " + string.Join(", ", reasons) + " · " + ChangePriority.Label;
        }
    }
    public string KindText => Kind switch { VaultItemKind.Login => "Inloggning", VaultItemKind.Link => "Länk", VaultItemKind.Note => "Anteckning", _ => "Konto" };
    public string Host => VaultItemMatcher.Host(Url) ?? "";
    public string ModifiedText => ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public bool Matches(string search) => string.IsNullOrWhiteSpace(search) ||
        new[] { Title, Url, Username, LinkedAccount, Notes, KindText }.Any(v => v.Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase));
}

/// <param name="Private">Shown only in the Privat section, after the master password or Windows Hello; never offered to the browser.</param>
public sealed record VaultItemDraft(string? Id, VaultItemKind Kind, string Title, string Url, string Username, string LinkedAccount, string Notes, bool Private = false);

/// <summary>Which leaked passwords to change first: accounts that reset other accounts, then money, domains and the rest.</summary>
public static class VaultItemRisk
{
    private static readonly (int Rank, string Label, string[] Hosts)[] Categories =
    [
        (1, "E-post", ["hotmail.", "outlook.", "live.com", "msn.com", "gmail.com", "mail.google.com", "mail.yahoo.", "proton.me", "protonmail.", "icloud.com/mail"]),
        (5, "Google Ads", ["ads.google.com"]),
        (2, "Microsoft/Apple/Google-konto", ["login.live.com", "login.microsoftonline.com", "account.microsoft.com", "microsoft.com", "apple.com", "icloud.com", "appleid.", "accounts.google.com", "google.com", "myaccount.google.com"]),
        (3, "Bank/betalning", ["bank", "swedbank", "seb.se", "handelsbanken", "nordea", "danskebank", "icabanken", "skandia", "lansforsakringar", "lf.se", "avanza", "nordnet", "klarna", "paypal", "swish", "stripe", "revolut", "wise.com", "collector", "resurs"]),
        (4, "Domän/DNS/hosting", ["websupport", "loopia", "bahnhof", "one.com", "binero", "godaddy", "namecheap", "cloudflare", "gandi", "hostinger", "porkbun", "simply.com", "registrar", "dns"]),
        (6, "Anthropic/Claude", ["anthropic.com", "claude.ai", "claude.com"]),
    ];
    public static (int Rank, string Label) Category(string host)
    {
        host = host.ToLowerInvariant();
        foreach (var (rank, label, hosts) in Categories)
            if (hosts.Any(h => host.Contains(h, StringComparison.Ordinal))) return (rank, label);
        return (7, "Övrigt");
    }
}

public static class PasswordStrength
{
    private static readonly string[] Common = ["password", "passw0rd", "lösenord", "losenord", "123456", "12345678", "qwerty", "asdfgh", "abc123", "111111", "letmein", "admin", "welcome", "iloveyou", "sommar", "vinter", "monkey", "dragon", "sverige", "hejhej"];
    /// <summary>Short, few character kinds, a well-known word or the user name: easy to guess.</summary>
    public static bool IsWeak(ReadOnlySpan<char> password, string username)
    {
        if (password.Length < 10) return true;
        int lower = 0, upper = 0, digit = 0, other = 0;
        foreach (var c in password) { if (char.IsLower(c)) lower = 1; else if (char.IsUpper(c)) upper = 1; else if (char.IsDigit(c)) digit = 1; else other = 1; }
        if (password.Length < 14 && lower + upper + digit + other < 3) return true;
        var distinct = new HashSet<char>(password.ToArray()); if (distinct.Count <= 4) return true;
        foreach (var word in Common) if (password.Contains(word, StringComparison.OrdinalIgnoreCase) && password.Length < word.Length + 8) return true;
        var name = username.Split('@')[0];
        return name.Length >= 4 && password.Contains(name, StringComparison.OrdinalIgnoreCase) && password.Length < name.Length + 8;
    }
}

public static class VaultItemMatcher
{
    /// <summary>Accepts "example.com" as https://example.com. Only http and https are web addresses.</summary>
    public static Uri? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.Host.Length > 0 ? uri : null;
    }
    public static string? Host(string? url) => Parse(url)?.IdnHost.ToLowerInvariant().TrimStart('.');
    /// <summary>Unencrypted http is accepted only for the user's own machine.</summary>
    public static bool IsLocalHost(string host) => host is "localhost" or "127.0.0.1" or "[::1]" or "::1" || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>Same host, or the page is a subdomain of the saved host. A saved https site is never offered to an http page.</summary>
    public static bool Matches(string savedUrl, Uri page)
    {
        var saved = Parse(savedUrl);
        if (saved is null || (page.Scheme != Uri.UriSchemeHttps && page.Scheme != Uri.UriSchemeHttp)) return false;
        if (saved.Scheme == Uri.UriSchemeHttps && page.Scheme != Uri.UriSchemeHttps) return false;
        var savedHost = saved.IdnHost.ToLowerInvariant(); var pageHost = page.IdnHost.ToLowerInvariant();
        return pageHost == savedHost || pageHost.EndsWith("." + savedHost, StringComparison.Ordinal);
    }
}

/// <summary>
/// Passwords, links, notes and linked accounts. Stored as one GnuPG-encrypted JSON document (items.gpg) next to the file catalog.
/// While the vault is open, each password is kept AES-GCM sealed under a random per-session key, and is only unsealed
/// for a master-password check, a confirmed browser fill or an encrypted save.
/// </summary>
public sealed class VaultItemStore : IDisposable
{
    public const int MaximumItems = 10000;
    public const int MaximumPasswordChars = 1024;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<string, byte[]> _sealed = new(StringComparer.Ordinal);
    private List<VaultItem> _items = [];
    private readonly byte[] _healthKey = RandomNumberGenerator.GetBytes(32);
    private readonly HashSet<string> _breached = new(StringComparer.Ordinal);
    private List<VaultItem>? _view;
    /// <summary>Items with computed password health. The raw list never carries <see cref="VaultItem.Issue"/>.</summary>
    public IReadOnlyList<VaultItem> Items => _view ??= Annotate();
    private void Changed() => _view = null;

    /// <summary>
    /// Reuse is found by comparing HMACs under a random per-session key, so no password is compared or kept in clear;
    /// strength is a local estimate. Nothing leaves the computer.
    /// </summary>
    private List<VaultItem> Annotate()
    {
        var macs = new Dictionary<string, string>(StringComparer.Ordinal); var counts = new Dictionary<string, int>(StringComparer.Ordinal); var weak = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _items.Where(i => i.HasPassword))
        {
            var password = Unseal(item.Id); var bytes = Encoding.UTF8.GetBytes(password);
            try
            {
                var mac = Convert.ToHexString(HMACSHA256.HashData(_healthKey, bytes));
                macs[item.Id] = mac; counts[mac] = counts.GetValueOrDefault(mac) + 1;
                if (PasswordStrength.IsWeak(password, item.Username)) weak.Add(item.Id);
            }
            finally { Array.Clear(password); CryptographicOperations.ZeroMemory(bytes); }
        }
        return _items.Select(item =>
        {
            var issue = PasswordIssue.None;
            if (macs.TryGetValue(item.Id, out var mac) && counts[mac] > 1) issue |= PasswordIssue.Reused;
            if (weak.Contains(item.Id)) issue |= PasswordIssue.Weak;
            if (_breached.Contains(item.Id)) issue |= PasswordIssue.Breached;
            return issue == PasswordIssue.None ? item : item with { Issue = issue };
        }).ToList();
    }

    /// <summary>SHA-1 per stored password, for the k-anonymous leak check only. Not kept after the check.</summary>
    internal List<(string Id, string Sha1)> PasswordSha1()
    {
        var result = new List<(string, string)>();
        foreach (var item in _items.Where(i => i.HasPassword))
        {
            var password = Unseal(item.Id); var bytes = Encoding.UTF8.GetBytes(password);
            try { result.Add((item.Id, Convert.ToHexString(SHA1.HashData(bytes)))); }
            finally { Array.Clear(password); CryptographicOperations.ZeroMemory(bytes); }
        }
        return result;
    }

    /// <summary>Result of an explicit leak check; cleared for an item when its password changes.</summary>
    public void MarkBreached(IEnumerable<string> ids) { foreach (var id in ids) _breached.Add(id); Changed(); }
    public long Revision { get; private set; }

    public VaultItem Get(string id) => _items.SingleOrDefault(i => i.Id == id) ?? throw new IOException("Posten finns inte i det öppna valvet.");

    /// <param name="markLeaked">Import: the password may be known to an attacker and must be changed.</param>
    public VaultItem Upsert(VaultItemDraft draft, ReadOnlySpan<char> password, bool replacePassword, bool removePassword = false, bool markLeaked = false)
    {
        Validate(draft.Kind, draft.Title, draft.Url, draft.Username, draft.LinkedAccount, draft.Notes);
        if (replacePassword) ValidateSecret(password);
        var now = DateTimeOffset.UtcNow;
        var existing = draft.Id is null ? null : Get(draft.Id);
        if (existing is null && _items.Count >= MaximumItems) throw new IOException("Valvet stöder högst 10 000 poster.");
        var id = existing?.Id ?? Guid.NewGuid().ToString("N");
        var hasPassword = removePassword ? false : replacePassword ? password.Length > 0 : existing?.HasPassword == true;
        // Only a different password clears the "change me" mark; saving the same leaked password again does not.
        var changed = removePassword || (replacePassword && !(existing is { HasPassword: true } && PasswordEquals(existing.Id, password)));
        var item = new VaultItem(id, draft.Kind, draft.Title.Trim(), draft.Url.Trim(), draft.Username.Trim(), draft.LinkedAccount.Trim(), draft.Notes, hasPassword, existing?.CreatedUtc ?? now, now,
            markLeaked || (existing?.MustChange == true && !changed), changed && !markLeaked ? now : existing?.PasswordChangedUtc, markLeaked ? now : existing?.ImportedUtc, Private: draft.Private);
        if (removePassword || (replacePassword && password.Length == 0)) Forget(id);
        else if (replacePassword) Seal(id, password);
        _items = existing is null ? [.. _items, item] : _items.Select(i => i.Id == id ? item : i).ToList();
        if (changed) _breached.Remove(id);
        Changed();
        return item;
    }

    public ImportSummary Import(IReadOnlyList<ImportedLogin> logins, IReadOnlyList<ImportedBookmark> bookmarks)
    {
        int created = 0, updated = 0, skipped = 0, flagged = 0;
        static string Key(string? host, string user) => (host ?? "") + "\n" + user.Trim().ToLowerInvariant();
        static string LinkKey(string url, bool isPrivate) => (isPrivate ? "p:" : "") + url.Trim().TrimEnd('/').ToLowerInvariant();
        var loginIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in _items.Where(i => i.Kind == VaultItemKind.Login)) loginIds.TryAdd(Key(VaultItemMatcher.Host(item.Url), item.Username), item.Id);
        var links = new HashSet<string>(_items.Where(i => i.Kind == VaultItemKind.Link).Select(i => LinkKey(i.Url, i.Private)), StringComparer.Ordinal);
        foreach (var login in logins)
        {
            try
            {
                var host = VaultItemMatcher.Host(login.Url); var key = Key(host, login.Username);
                if (host is null) { skipped++; continue; }
                if (loginIds.TryGetValue(key, out var id))
                {
                    var existing = Get(id);
                    var same = existing.HasPassword && PasswordEquals(id, login.Password);
                    // A password that was in the browser counts as leaked, also when NordVault already had it.
                    Upsert(new VaultItemDraft(id, existing.Kind, existing.Title, existing.Url, existing.Username, existing.LinkedAccount, existing.Notes), login.Password, !same, markLeaked: true);
                    flagged++; if (same) skipped++; else updated++;
                    continue;
                }
                var item = Upsert(new VaultItemDraft(null, VaultItemKind.Login, host, login.Url.Trim(), login.Username.Trim(), "", ""), login.Password, true, markLeaked: true);
                loginIds[key] = item.Id; created++; flagged++;
            }
            catch (ArgumentException) { skipped++; }
        }
        foreach (var bookmark in bookmarks)
        {
            try
            {
                if (!links.Add(LinkKey(bookmark.Url, bookmark.Private))) { skipped++; continue; }
                Upsert(new VaultItemDraft(null, VaultItemKind.Link, bookmark.Title, bookmark.Url, "", "", bookmark.Note.Length > 0 ? bookmark.Note : bookmark.Folder.Length > 0 ? "Firefox-mapp: " + bookmark.Folder : "", bookmark.Private), [], false);
                created++;
            }
            catch (ArgumentException) { skipped++; }
        }
        return new ImportSummary(created, updated, skipped, flagged);
    }

    /// <summary>Moves links and notes into or out of the Privat section. Logins stay where they are.</summary>
    public VaultItem SetPrivate(IEnumerable<string> ids, bool isPrivate)
    {
        var wanted = ids.ToHashSet(StringComparer.Ordinal); VaultItem? last = null;
        _items = _items.Select(i => wanted.Contains(i.Id) && i.Kind is VaultItemKind.Link or VaultItemKind.Note ? (last = i with { Private = isPrivate, ModifiedUtc = DateTimeOffset.UtcNow }) : i).ToList();
        Changed();
        return last ?? throw new IOException("Inga länkar valdes.");
    }

    public void Remove(string id) { Get(id); Forget(id); _breached.Remove(id); _items = _items.Where(i => i.Id != id).ToList(); Changed(); }

    /// <summary>Caller must clear the returned array.</summary>
    public char[] Unseal(string id)
    {
        if (!_sealed.TryGetValue(id, out var box)) return [];
        var plain = new byte[box.Length - 28];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(box.AsSpan(0, 12), box.AsSpan(28), box.AsSpan(12, 16), plain, Encoding.ASCII.GetBytes(id));
            return Encoding.UTF8.GetChars(plain);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public bool PasswordEquals(string id, ReadOnlySpan<char> candidate)
    {
        var current = Unseal(id);
        try { return current.AsSpan().SequenceEqual(candidate); } finally { Array.Clear(current); }
    }

    private void Seal(string id, ReadOnlySpan<char> password)
    {
        var plain = new byte[Encoding.UTF8.GetByteCount(password)];
        try
        {
            Encoding.UTF8.GetBytes(password, plain);
            var box = new byte[28 + plain.Length];
            RandomNumberGenerator.Fill(box.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(box.AsSpan(0, 12), plain, box.AsSpan(28), box.AsSpan(12, 16), Encoding.ASCII.GetBytes(id));
            Forget(id); _sealed[id] = box;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private void Forget(string id) { if (_sealed.Remove(id, out var old)) CryptographicOperations.ZeroMemory(old); }

    public static void Validate(VaultItemKind kind, string title, string url, string username, string linked, string notes)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentException("Okänd posttyp.");
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200 || title.Any(char.IsControl)) throw new ArgumentException("Ange en titel med högst 200 tecken.");
        if (url.Length > 2048 || url.Any(char.IsControl)) throw new ArgumentException("Webbadressen är för lång eller ogiltig.");
        if (url.Trim().Length > 0 && VaultItemMatcher.Parse(url) is null) throw new ArgumentException("Webbadressen måste vara en http- eller https-adress, till exempel https://exempel.se.");
        if ((kind is VaultItemKind.Login or VaultItemKind.Link) && url.Trim().Length == 0) throw new ArgumentException("Ange webbplatsens adress.");
        if (username.Length > 512 || username.Any(char.IsControl)) throw new ArgumentException("Användarnamnet är för långt eller ogiltigt.");
        if (linked.Length > 200 || linked.Any(char.IsControl)) throw new ArgumentException("Kopplat konto är för långt eller ogiltigt.");
        if (notes.Length > 100_000) throw new ArgumentException("Texten får ha högst 100 000 tecken.");
    }
    public static void ValidateSecret(ReadOnlySpan<char> password)
    {
        if (password.Length > MaximumPasswordChars) throw new ArgumentException("Lösenordet får ha högst 1 024 tecken.");
        foreach (var c in password) if (char.IsControl(c)) throw new ArgumentException("Lösenordet får inte innehålla kontrolltecken.");
    }

    /// <summary>Plaintext JSON for GnuPG. Passwords are written from spans, so no password strings are created. Caller zeroes the result.</summary>
    public byte[] Serialize(string vaultId)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("Version", 1); writer.WriteString("VaultId", vaultId); writer.WriteNumber("Revision", Revision + 1);
        writer.WriteStartArray("Items");
        foreach (var item in _items)
        {
            writer.WriteStartObject();
            writer.WriteString("Id", item.Id); writer.WriteString("Kind", item.Kind.ToString()); writer.WriteString("Title", item.Title);
            writer.WriteString("Url", item.Url); writer.WriteString("Username", item.Username); writer.WriteString("LinkedAccount", item.LinkedAccount);
            writer.WriteString("Notes", item.Notes);
            if (item.HasPassword) { var password = Unseal(item.Id); try { writer.WriteString("Password", password); } finally { Array.Clear(password); } }
            writer.WriteString("CreatedUtc", item.CreatedUtc); writer.WriteString("ModifiedUtc", item.ModifiedUtc);
            if (item.MustChange) writer.WriteBoolean("MustChange", true);
            if (item.Private) writer.WriteBoolean("Private", true);
            if (item.PasswordChangedUtc is { } changed) writer.WriteString("PasswordChangedUtc", changed);
            if (item.ImportedUtc is { } imported) writer.WriteString("ImportedUtc", imported);
            writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush();
        var result = buffer.WrittenSpan.ToArray();
        buffer.Clear(); // zeroes the written region
        return result;
    }

    public void CommitRevision() => Revision++;

    internal sealed class Snapshot(List<VaultItem> items, Dictionary<string, byte[]> sealedCopies)
    {
        public List<VaultItem> Items { get; } = items; public Dictionary<string, byte[]> Sealed { get; } = sealedCopies;
        public void Discard() { foreach (var box in Sealed.Values) CryptographicOperations.ZeroMemory(box); Sealed.Clear(); }
    }
    internal Snapshot Capture() => new([.. _items], _sealed.ToDictionary(p => p.Key, p => (byte[])p.Value.Clone(), StringComparer.Ordinal));
    internal void Restore(Snapshot snapshot)
    {
        foreach (var box in _sealed.Values) CryptographicOperations.ZeroMemory(box);
        _sealed.Clear(); foreach (var pair in snapshot.Sealed) _sealed[pair.Key] = pair.Value;
        _items = snapshot.Items; Changed();
    }

    public static VaultItemStore Read(ReadOnlySpan<byte> json, string vaultId)
    {
        var store = new VaultItemStore(); var ids = new HashSet<string>(StringComparer.Ordinal);
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
        int? version = null; string? owner = null;
        Expect(ref reader, JsonTokenType.StartObject);
        while (Next(ref reader) == JsonTokenType.PropertyName)
        {
            var name = reader.GetString(); Next(ref reader);
            switch (name)
            {
                case "Version": version = reader.GetInt32(); break;
                case "VaultId": owner = reader.GetString(); break;
                case "Revision": store.Revision = reader.GetInt64(); break;
                case "Items":
                    if (reader.TokenType != JsonTokenType.StartArray) throw Invalid();
                    while (Next(ref reader) == JsonTokenType.StartObject)
                    {
                        if (ids.Count >= MaximumItems) throw Invalid();
                        ReadItem(ref reader, store, ids);
                    }
                    if (reader.TokenType != JsonTokenType.EndArray) throw Invalid();
                    break;
                default: reader.Skip(); break;
            }
        }
        if (version != 1 || owner != vaultId) throw new IOException("Lösenordsfilen hör inte till detta valv eller har ett format som inte stöds.");
        return store;
    }

    private static void ReadItem(ref Utf8JsonReader reader, VaultItemStore store, HashSet<string> ids)
    {
        string? id = null, kind = null, title = null, url = "", user = "", linked = "", notes = ""; DateTimeOffset created = default, modified = default; DateTimeOffset? passwordChanged = null, importedAt = null; var mustChange = false; var isPrivate = false;
        char[]? password = null;
        try
        {
            while (Next(ref reader) == JsonTokenType.PropertyName)
            {
                var name = reader.GetString(); Next(ref reader);
                if (name == "Password")
                {
                    var encodedLength = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
                    if (reader.TokenType != JsonTokenType.String || encodedLength > MaximumPasswordChars * 6) throw Invalid();
                    var scratch = new char[encodedLength];
                    var length = reader.CopyString(scratch);
                    if (password is not null) Array.Clear(password);
                    password = scratch[..length]; Array.Clear(scratch);
                    continue;
                }
                if (name == "MustChange" && reader.TokenType is JsonTokenType.True or JsonTokenType.False) { mustChange = reader.GetBoolean(); continue; }
                if (name == "Private" && reader.TokenType is JsonTokenType.True or JsonTokenType.False) { isPrivate = reader.GetBoolean(); continue; }
                if (reader.TokenType != JsonTokenType.String) { reader.Skip(); continue; }
                if (name == "PasswordChangedUtc") { passwordChanged = reader.GetDateTimeOffset(); continue; }
                if (name == "ImportedUtc") { importedAt = reader.GetDateTimeOffset(); continue; }
                switch (name)
                {
                    case "Id": id = reader.GetString(); break; case "Kind": kind = reader.GetString(); break; case "Title": title = reader.GetString(); break;
                    case "Url": url = reader.GetString(); break; case "Username": user = reader.GetString(); break; case "LinkedAccount": linked = reader.GetString(); break;
                    case "Notes": notes = reader.GetString(); break; case "CreatedUtc": created = reader.GetDateTimeOffset(); break; case "ModifiedUtc": modified = reader.GetDateTimeOffset(); break;
                }
            }
            if (id is null || !Guid.TryParseExact(id, "N", out _) || !ids.Add(id) || !Enum.TryParse<VaultItemKind>(kind, false, out var parsed) || !Enum.IsDefined(parsed) || title is null)
                throw Invalid();
            Validate(parsed, title, url ?? "", user ?? "", linked ?? "", notes ?? "");
            if (password is not null) ValidateSecret(password);
            var hasPassword = password is { Length: > 0 };
            if (hasPassword) store.Seal(id, password);
            store._items.Add(new VaultItem(id, parsed, title, url ?? "", user ?? "", linked ?? "", notes ?? "", hasPassword, created, modified, mustChange, passwordChanged, importedAt, Private: isPrivate));
        }
        catch (ArgumentException) { throw Invalid(); }
        finally { if (password is not null) Array.Clear(password); }
    }

    private static JsonTokenType Next(ref Utf8JsonReader reader) { if (!reader.Read()) throw Invalid(); return reader.TokenType; }
    private static void Expect(ref Utf8JsonReader reader, JsonTokenType type) { if (Next(ref reader) != type) throw Invalid(); }
    private static IOException Invalid() => new("Valvets lösenordsfil är ogiltig.");

    public void Dispose()
    {
        foreach (var box in _sealed.Values) CryptographicOperations.ZeroMemory(box);
        _sealed.Clear(); _items = []; _view = null; _breached.Clear(); CryptographicOperations.ZeroMemory(_key); CryptographicOperations.ZeroMemory(_healthKey);
    }
}
