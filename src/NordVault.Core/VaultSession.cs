using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace NordVault.Core;

/// <summary>Portable GnuPG-encrypted files and authenticated, encrypted catalog, all under the vault key (see <see cref="VaultKeyring"/>). No DPAPI binding.</summary>
[SupportedOSPlatform("windows")]
public sealed class VaultSession : IDisposable
{
    private readonly FileCryptoService _crypto;
    private readonly FileStream _lock;
    private readonly char[] _key; // vault key: GnuPG passphrase for catalog, items and entries
    private readonly SemaphoreSlim _operations = new(1, 1);
    private VaultCatalog _catalog;
    private VaultItemStore _items;
    private int _failedMasterChecks;
    private bool _disposed;
    public string Root { get; }
    public IReadOnlyList<VaultEntry> Entries => _catalog.Entries;
    public IReadOnlyList<VaultItem> Items => _items.Items;
    public bool IsBusy => _operations.CurrentCount == 0;
    /// <summary>Three wrong master passwords in an open session: the caller must lock the vault.</summary>
    public bool MasterPasswordLockout => _failedMasterChecks >= 3;
    private string CatalogPath => Path.Combine(Root, "catalog.gpg");
    private string EntryPath(string id) => Path.Combine(Root, "entries", id + ".gpg");

    private VaultSession(string root, FileCryptoService crypto, FileStream fileLock, ReadOnlyMemory<char> key, VaultCatalog catalog, VaultItemStore items)
    { Root = root; _crypto = crypto; _lock = fileLock; _key = key.ToArray(); _catalog = catalog; _items = items; }

    public string VaultId => _catalog.VaultId;
    /// <summary>Set when the vault was opened with a recovery code: the user must choose a new master password, which uses up the code.</summary>
    public string? UsedRecoveryCapsule { get; private set; }
    public int RecoveryCodeCount => VaultKeyring.RecoveryCapsules(Root).Length;
    public bool HelloEnabled => WindowsHello.IsEnabled(Root);

    public static Task<VaultSession> OpenAsync(string root, GnuPgRuntime runtime, ReadOnlyMemory<char> password, CancellationToken cancellation = default) =>
        OpenCoreAsync(root, runtime, async (crypto, folder) =>
        {
            var capsule = VaultKeyring.PasswordCapsule(folder);
            if (File.Exists(capsule)) { var (key, id) = await VaultKeyring.ReadAsync(crypto, capsule, password, cancellation); return (key, id, null); }
            return (password.ToArray(), null, null); // 0.3.x vault: the master password is the vault key
        }, cancellation, migratePassword: password);

    /// <summary>"Glömt huvudlösenordet?": any unused recovery code opens the vault. Choose a new master password next.</summary>
    public static Task<VaultSession> OpenWithRecoveryCodeAsync(string root, GnuPgRuntime runtime, ReadOnlyMemory<char> typedCode, CancellationToken cancellation = default) =>
        OpenCoreAsync(root, runtime, async (crypto, folder) =>
        {
            var code = VaultKeyring.Normalize(typedCode.Span);
            try
            {
                foreach (var capsule in VaultKeyring.RecoveryCapsules(folder))
                {
                    try { var (key, id) = await VaultKeyring.ReadAsync(crypto, capsule, code, cancellation); return (key, id, capsule); }
                    catch (Exception error) when (error is IOException or InvalidOperationException) { }
                }
            }
            finally { Array.Clear(code); }
            throw new IOException("Koden stämmer inte med någon oanvänd återställningskod för valvet.");
        }, cancellation);

    /// <summary>Shows the Windows Hello prompt. Only for a vault where Hello was turned on, on this computer.</summary>
    public static Task<VaultSession> OpenWithHelloAsync(string root, GnuPgRuntime runtime, IntPtr window, CancellationToken cancellation = default) =>
        OpenCoreAsync(root, runtime, async (_, folder) =>
        {
            var (key, id) = await Task.Run(() => WindowsHello.Unlock(folder, window, "Lås upp NordVault-valvet"), cancellation);
            return (key, id, null);
        }, cancellation);

    private static async Task<VaultSession> OpenCoreAsync(string root, GnuPgRuntime runtime, Func<FileCryptoService, string, Task<(char[] Key, string? VaultId, string? Recovery)>> unlock,
        CancellationToken cancellation, ReadOnlyMemory<char>? migratePassword = null)
    {
        root = FileCryptoPaths.Validate(root).TrimEnd('\\');
        if (root.Length < 4) throw new IOException("Välj en egen undermapp för valvet.");
        var crypto = new FileCryptoService(runtime);
        var fileLock = Lock(root);
        char[]? key = null;
        try
        {
            var (unlockedKey, capsuleVaultId, recovery) = await unlock(crypto, root); key = unlockedKey;
            using var plaintext = await crypto.DecryptMetadataAsync(Path.Combine(root, "catalog.gpg"), key, cancellation);
            var catalog = VaultCatalog.Read(plaintext.Memory.Span);
            if (capsuleVaultId is not null && capsuleVaultId != catalog.VaultId) throw new IOException("Nyckelkapseln hör till ett annat valv.");
            var items = new VaultItemStore();
            var itemsPath = Path.Combine(root, "items.gpg");
            if (File.Exists(itemsPath))
            {
                items.Dispose();
                using var itemPlaintext = await crypto.DecryptMetadataAsync(itemsPath, key, cancellation);
                items = VaultItemStore.Read(itemPlaintext.Memory.Span, catalog.VaultId);
            }
            var session = new VaultSession(root, crypto, fileLock, key, catalog, items) { UsedRecoveryCapsule = recovery };
            // 0.3.x vault: add a password capsule for the same key, so recovery codes and Hello can be added. Older versions still open it.
            if (capsuleVaultId is null && migratePassword is { } password)
            {
                try { await VaultKeyring.WriteAsync(crypto, VaultKeyring.PasswordCapsule(root), catalog.VaultId, key, password, cancellation); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { } // read-only medium: keeps working as 0.3.x
            }
            return session;
        }
        catch { fileLock.Dispose(); throw; }
        finally { if (key is not null) Array.Clear(key); }
    }

    public static async Task<VaultSession> CreateAsync(string root, GnuPgRuntime runtime, ReadOnlyMemory<char> password, CancellationToken cancellation = default)
    {
        FileCryptoService.ValidatePassword(password.Span, false); runtime.Verify();
        root = FileCryptoPaths.Validate(root).TrimEnd('\\');
        if (root.Length < 4) throw new IOException("Välj en egen undermapp för valvet.");
        Directory.CreateDirectory(root);
        FileCryptoPaths.AssertNoReparsePoints(root);
        var fileLock = Lock(root);
        try
        {
            if (Directory.EnumerateFileSystemEntries(root).Any(p => Path.GetFileName(p) != ".session.lock"))
                throw new IOException("Skapa ett nytt valv i en tom mapp. Befintligt innehåll ändras inte.");
            Directory.CreateDirectory(Path.Combine(root, "entries"));
            var crypto = new FileCryptoService(runtime);
            var catalog = VaultCatalog.Empty();
            var key = VaultKeyring.NewVaultKey();
            try
            {
                await VaultKeyring.WriteAsync(crypto, VaultKeyring.PasswordCapsule(root), catalog.VaultId, key, password, cancellation);
                var encoded = JsonSerializer.SerializeToUtf8Bytes(catalog);
                try { await crypto.EncryptMetadataAsync(encoded, Path.Combine(root, "catalog.gpg"), key, cancellation); }
                finally { CryptographicOperations.ZeroMemory(encoded); }
                PortableCipherAccess.Inherit(Path.Combine(root,"catalog.gpg"));
                return new(root, crypto, fileLock, key, catalog, new VaultItemStore());
            }
            finally { Array.Clear(key); }
        }
        catch { fileLock.Dispose(); throw; }
    }

    private static FileStream Lock(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Valvmappen saknas.");
        var path = Path.Combine(root, ".session.lock"); FileCryptoPaths.AssertNoReparsePoints(path);
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try { FileCryptoPaths.VerifyHandlePath(stream.SafeFileHandle, path); return stream; } catch { stream.Dispose(); throw; }
        }
        catch (IOException ex) { throw new IOException("Valvet kan redan vara öppet i ett annat NordVault, eller mappen är inte skrivbar.", ex); }
    }

    public async Task<VaultEntry> ImportAsync(string input, CancellationToken cancellation = default, IProgress<FileCryptoProgress>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operations.WaitAsync(cancellation);
        string? encrypted = null; var catalogCommitted = false;
        try
        {
            if (_catalog.Entries.Length >= 5000) throw new IOException("Den här versionen stöder högst 5 000 valvfiler.");
            input = FileCryptoPaths.Validate(input);
            if (input.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Importera inte valvets egna programfiler eller krypterade objekt i samma valv.");
            var name = Path.GetFileName(input);
            if (!VaultCatalog.IsSafeName(name)) throw new IOException("Filnamnet kan inte lagras säkert i valvet.");
            using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
            FileCryptoPaths.VerifyHandlePath(source.SafeFileHandle, input);
            var id = Guid.NewGuid().ToString("N"); encrypted = EntryPath(id);
            var result = await _crypto.EncryptAsync(input, encrypted, _key, cancellation, progress);
            if (result.CleanupWarning is not null) throw new IOException(result.CleanupWarning);
            PortableCipherAccess.Inherit(encrypted);
            var entry = new VaultEntry(id, name, source.Length, await HashAsync(encrypted,cancellation), DateTimeOffset.UtcNow);
            var next = _catalog with { Entries = [.. _catalog.Entries, entry] };
            await CommitAsync(next, cancellation); catalogCommitted = true; _catalog = next;
            return entry;
        }
        finally
        {
            try { if (!catalogCommitted && encrypted is not null && File.Exists(encrypted)) File.Delete(encrypted); }
            finally { _operations.Release(); }
        }
    }

    public async Task<FileCryptoResult> ExportAsync(string id, string output, CancellationToken cancellation = default, IProgress<FileCryptoProgress>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); await _operations.WaitAsync(cancellation);
        try
        {
            var entry = _catalog.Entries.SingleOrDefault(e => e.Id == id) ?? throw new IOException("Filen finns inte i det öppna valvet.");
            var input = EntryPath(entry.Id); FileCryptoPaths.Validate(input);
            using var held = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
            FileCryptoPaths.VerifyHandlePath(held.SafeFileHandle, input);
            var actualHash=await SHA256.HashDataAsync(held,cancellation);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(entry.CipherSha256), actualHash))
                throw new IOException("Det krypterade objektet matchar inte valvets autentiserade innehållsförteckning.");
            output = FileCryptoPaths.Validate(output);
            if (output.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("Spara klartext utanför valvmappen.");
            return await _crypto.DecryptAsync(input, output, _key, cancellation, progress);
        }
        finally { _operations.Release(); }
    }

    private Task CommitAsync(VaultCatalog catalog, CancellationToken cancellation) =>
        CommitFileAsync(JsonSerializer.SerializeToUtf8Bytes(catalog), "catalog", cancellation);

    /// <summary>Encrypts to a temporary file, keeps the previous version as name.previous.gpg, then replaces name.gpg. Zeroes bytes.</summary>
    private async Task CommitFileAsync(byte[] bytes, string name, CancellationToken cancellation)
    {
        var target = Path.Combine(Root, name + ".gpg"); var previous = Path.Combine(Root, name + ".previous.gpg");
        var temporary = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N") + ".gpg");
        try
        {
            await _crypto.EncryptMetadataAsync(bytes, temporary, _key, cancellation);
            PortableCipherAccess.Inherit(temporary);
            cancellation.ThrowIfCancellationRequested(); FileCryptoPaths.AssertNoReparsePoints(target);
            FileCryptoPaths.AssertNoReparsePoints(previous);
            if (File.Exists(target)) File.Copy(target, previous, true);
            File.Move(temporary, target, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Removes a file from the catalog first, then its encrypted object.</summary>
    public async Task DeleteAsync(string id, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); await _operations.WaitAsync(cancellation);
        try
        {
            var entry = _catalog.Entries.SingleOrDefault(e => e.Id == id) ?? throw new IOException("Filen finns inte i det öppna valvet.");
            var next = _catalog with { Entries = _catalog.Entries.Where(e => e.Id != id).ToArray() };
            await CommitAsync(next, cancellation); _catalog = next;
            var path = EntryPath(entry.Id); FileCryptoPaths.AssertNoReparsePoints(path);
            if (File.Exists(path)) File.Delete(path);
        }
        finally { _operations.Release(); }
    }

    /// <summary>Creates or edits a login, link, note or linked account. With replacePassword, an empty password removes it.</summary>
    public Task<VaultItem> SaveItemAsync(VaultItemDraft draft, ReadOnlyMemory<char> password, bool replacePassword, CancellationToken cancellation = default) =>
        ChangeItemsAsync(store => store.Upsert(draft, password.Span, replacePassword), cancellation);

    public Task SetPrivateAsync(IReadOnlyCollection<string> ids, bool isPrivate, CancellationToken cancellation = default) =>
        ChangeItemsAsync(store => store.SetPrivate(ids, isPrivate), cancellation);

    /// <summary>The Privat section: needs the master password or Windows Hello again, also in an open session.</summary>
    public async Task<bool> UnlockPrivateAsync(ReadOnlyMemory<char> masterPassword, IntPtr helloWindow, bool useHello, CancellationToken cancellation = default) =>
        useHello ? await ConfirmWithHelloAsync(helloWindow, "Öppna Privat i NordVault") : await VerifyMasterPasswordAsync(masterPassword, cancellation);

    public Task DeleteItemAsync(string id, CancellationToken cancellation = default) =>
        ChangeItemsAsync(store => { var item = store.Get(id); store.Remove(id); return item; }, cancellation);

    /// <summary>Firefox logins and bookmarks in one encrypted commit. Existing site + user name is updated, identical entries are skipped.</summary>
    public Task<ImportSummary> ImportItemsAsync(IReadOnlyList<ImportedLogin> logins, IReadOnlyList<ImportedBookmark> bookmarks, CancellationToken cancellation = default) =>
        ChangeItemsAsync(store => store.Import(logins, bookmarks), cancellation);

    private async Task<T> ChangeItemsAsync<T>(Func<VaultItemStore, T> change, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); await _operations.WaitAsync(cancellation);
        var snapshot = _items.Capture();
        try
        {
            var item = change(_items);
            await CommitFileAsync(_items.Serialize(_catalog.VaultId), "items", cancellation);
            _items.CommitRevision(); snapshot.Discard();
            return item;
        }
        catch { _items.Restore(snapshot); throw; }
        finally { _operations.Release(); }
    }

    public void MarkBreached(IEnumerable<string> ids) { ObjectDisposedException.ThrowIf(_disposed, this); _items.MarkBreached(ids); }

    /// <summary>Checks every stored password against known leaks. <paramref name="fetchRange"/> receives only 5-character SHA-1 prefixes.</summary>
    public async Task<int> CheckBreachesAsync(Func<string, CancellationToken, Task<string>> fetchRange, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var hashes = _items.PasswordSha1(); var breached = new List<string>();
        var groups = hashes.GroupBy(h => h.Sha1[..5]).ToArray(); var done = 0;
        foreach (var group in groups)
        {
            progress?.Report($"Läckkontroll {++done} av {groups.Length}…");
            var range = await fetchRange(group.Key, cancellation);
            breached.AddRange(group.Where(h => PwnedPasswords.Contains(range, h.Sha1[5..])).Select(h => h.Id));
        }
        ObjectDisposedException.ThrowIf(_disposed, this);
        _items.MarkBreached(breached);
        return breached.Distinct().Count();
    }

    public IReadOnlyList<VaultItem> FindLogins(Uri page) =>
        _items.Items.Where(i => i.Kind is VaultItemKind.Login or VaultItemKind.Account && i.HasPassword && VaultItemMatcher.Matches(i.Url, page)).ToArray();

    /// <summary>Saves a login captured in the browser: same site and user name updates, otherwise a new login is created.</summary>
    public async Task<string> SaveBrowserLoginAsync(Uri page, string username, ReadOnlyMemory<char> password, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (password.IsEmpty) throw new ArgumentException("Lösenordet saknas.");
        var origin = page.GetLeftPart(UriPartial.Authority);
        username = username.Trim();
        var existing = _items.Items.FirstOrDefault(i => i.Kind == VaultItemKind.Login && VaultItemMatcher.Host(i.Url) == VaultItemMatcher.Host(origin) &&
            string.Equals(i.Username, username, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && existing.HasPassword && _items.PasswordEquals(existing.Id, password.Span)) return "unchanged";
        var draft = existing is null
            ? new VaultItemDraft(null, VaultItemKind.Login, page.IdnHost, origin, username, "", "")
            : new VaultItemDraft(existing.Id, existing.Kind, existing.Title, existing.Url, existing.Username, existing.LinkedAccount, existing.Notes);
        await SaveItemAsync(draft, password, true, cancellation);
        return existing is null ? "created" : "updated";
    }

    /// <summary>
    /// Reading a stored password in an open session needs the master password again: it must open the password capsule
    /// and give this vault's key. Three misses lock out.
    /// </summary>
    public async Task<bool> VerifyMasterPasswordAsync(ReadOnlyMemory<char> candidate, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (MasterPasswordLockout || candidate.IsEmpty) { if (!candidate.IsEmpty) _failedMasterChecks++; return false; }
        var ok = false;
        var capsule = VaultKeyring.PasswordCapsule(Root);
        if (File.Exists(capsule))
        {
            try { var (key, _) = await VaultKeyring.ReadAsync(_crypto, capsule, candidate, cancellation); try { ok = SameKey(key); } finally { Array.Clear(key); } }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException) { }
        }
        else ok = SameKey(candidate.Span); // 0.3.x vault on a read-only medium: the master password is the key
        _failedMasterChecks = ok ? 0 : _failedMasterChecks + 1;
        return ok;
    }
    private bool SameKey(ReadOnlySpan<char> other)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(_key); var b = new byte[System.Text.Encoding.UTF8.GetByteCount(other)];
        try { System.Text.Encoding.UTF8.GetBytes(other, b); return CryptographicOperations.FixedTimeEquals(SHA256.HashData(a), SHA256.HashData(b)); }
        finally { CryptographicOperations.ZeroMemory(a); CryptographicOperations.ZeroMemory(b); }
    }

    /// <summary>Windows Hello as approval instead of typing the master password (shows the Hello prompt).</summary>
    public async Task<bool> ConfirmWithHelloAsync(IntPtr window, string message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var (key, id) = await Task.Run(() => WindowsHello.Unlock(Root, window, message));
            try { return id == VaultId && SameKey(key); } finally { Array.Clear(key); }
        }
        catch (IOException) { return false; }
    }

    /// <summary>Caller clears the result. Throws on a wrong master password.</summary>
    public async Task<char[]> RevealPasswordAsync(string id, ReadOnlyMemory<char> masterPassword, CancellationToken cancellation = default)
    {
        _items.Get(id);
        if (!await VerifyMasterPasswordAsync(masterPassword, cancellation)) throw new UnauthorizedAccessException("Fel huvudlösenord.");
        return _items.Unseal(id);
    }
    /// <summary>After a successful Windows Hello approval. Caller clears the result.</summary>
    public async Task<char[]> RevealPasswordWithHelloAsync(string id, IntPtr window)
    {
        var item = _items.Get(id);
        if (!await ConfirmWithHelloAsync(window, "Visa lösenordet för " + item.Title)) throw new UnauthorizedAccessException("Windows Hello godkände inte.");
        return _items.Unseal(id);
    }

    /// <summary>
    /// New master password. Needs the current one, or a session opened with a recovery code (that code is then used up).
    /// Windows Hello is turned off and must be turned on again. The vault key and the files stay the same.
    /// </summary>
    public async Task ChangeMasterPasswordAsync(ReadOnlyMemory<char>? currentPassword, ReadOnlyMemory<char> newPassword, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FileCryptoService.ValidatePassword(newPassword.Span, false);
        if (UsedRecoveryCapsule is null && (currentPassword is not { } current || !await VerifyMasterPasswordAsync(current, cancellation)))
            throw new UnauthorizedAccessException("Fel nuvarande huvudlösenord.");
        await _operations.WaitAsync(cancellation);
        try
        {
            await VaultKeyring.WriteAsync(_crypto, VaultKeyring.PasswordCapsule(Root), VaultId, _key, newPassword, cancellation);
            if (UsedRecoveryCapsule is { } used && File.Exists(used)) File.Delete(used);
            UsedRecoveryCapsule = null;
            WindowsHello.Disable(Root, VaultId);
        }
        finally { _operations.Release(); }
    }

    /// <summary>
    /// New one-time recovery codes; all earlier codes stop working. Shown once to the user and never stored in clear.
    /// Caller clears every returned code.
    /// </summary>
    public async Task<List<char[]>> CreateRecoveryCodesAsync(ReadOnlyMemory<char> masterPassword, int count = 5, CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count is < 1 or > VaultKeyring.MaximumRecoveryCodes) throw new ArgumentOutOfRangeException(nameof(count));
        if (!await VerifyMasterPasswordAsync(masterPassword, cancellation)) throw new UnauthorizedAccessException("Fel huvudlösenord.");
        await _operations.WaitAsync(cancellation);
        var codes = new List<char[]>(); var written = new List<string>();
        try
        {
            var old = VaultKeyring.RecoveryCapsules(Root);
            for (var i = 0; i < count; i++)
            {
                var code = VaultKeyring.NewRecoveryCode(); codes.Add(code);
                var path = Path.Combine(VaultKeyring.KeysFolder(Root), "recovery-" + Guid.NewGuid().ToString("N") + ".gpg");
                await VaultKeyring.WriteAsync(_crypto, path, VaultId, _key, code, cancellation); written.Add(path);
            }
            foreach (var path in old) File.Delete(path);
            return codes;
        }
        catch
        {
            foreach (var code in codes) Array.Clear(code);
            foreach (var path in written) if (File.Exists(path)) File.Delete(path);
            throw;
        }
        finally { _operations.Release(); }
    }

    /// <summary>Turns on Windows Hello for this vault on this computer (shows the Hello prompt). Needs the master password.</summary>
    public async Task EnableHelloAsync(ReadOnlyMemory<char> masterPassword, IntPtr window, CancellationToken cancellation = default)
    {
        if (!await VerifyMasterPasswordAsync(masterPassword, cancellation)) throw new UnauthorizedAccessException("Fel huvudlösenord.");
        var key = _key.ToArray();
        try { await Task.Run(() => WindowsHello.Enable(Root, VaultId, key, window, "Slå på Windows Hello för NordVault"), cancellation); }
        finally { Array.Clear(key); }
    }
    public void DisableHello() { ObjectDisposedException.ThrowIf(_disposed, this); WindowsHello.Disable(Root, VaultId); }

    /// <summary>Only after the user has confirmed the fill in NordVault's own window. Caller clears the result.</summary>
    public char[] ReleaseForConfirmedFill(string id, Uri page)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var item = _items.Get(id);
        if (!VaultItemMatcher.Matches(item.Url, page)) throw new IOException("Inloggningen hör inte till den här webbplatsen.");
        return _items.Unseal(id);
    }

    internal static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    internal static async Task<string> HashAsync(string path,CancellationToken cancellation) { using var input = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(input,cancellation)); }
    public void Dispose()
    {
        if (_disposed) return;
        if (IsBusy) throw new InvalidOperationException("Vänta eller avbryt valvåtgärden innan du låser.");
        _disposed = true; Array.Clear(_key); _items.Dispose(); _catalog = VaultCatalog.Empty(); _lock.Dispose(); _operations.Dispose();
    }
}
