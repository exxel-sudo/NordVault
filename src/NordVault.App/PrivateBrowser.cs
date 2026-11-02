using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using NordVault.Core;

namespace NordVault.App;

/// <summary>Opens a link in a private window: Firefox (-private-window), Chrome (--incognito) or Edge (-inprivate).</summary>
internal static class PrivateBrowser
{
    public sealed record Browser(string Id, string Name, string Flag);
    public static readonly Browser[] All = [new("firefox", "Firefox (privat fönster)", "-private-window"), new("chrome", "Chrome (inkognito)", "--incognito"), new("msedge", "Edge (InPrivate)", "-inprivate")];

    /// <summary>Installed path from Windows "App Paths", then the usual install folders.</summary>
    public static string? PathOf(Browser browser)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + browser.Id + ".exe");
                if (key?.GetValue("") is string path && File.Exists(path.Trim('"'))) return path.Trim('"');
            }
            catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        var folders = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
        var relative = browser.Id switch { "firefox" => @"Mozilla Firefox\firefox.exe", "chrome" => @"Google\Chrome\Application\chrome.exe", _ => @"Microsoft\Edge\Application\msedge.exe" };
        return folders.Where(f => f.Length > 0).Select(f => Path.Combine(f, relative)).FirstOrDefault(File.Exists);
    }
    public static Browser[] Installed => All.Where(b => PathOf(b) is not null).ToArray();

    /// <summary>Only http and https addresses; the address is passed as one argument, never through a shell.</summary>
    public static void Open(Browser browser, string url)
    {
        if (VaultItemMatcher.Parse(url) is not { } uri || !url.Contains("://", StringComparison.Ordinal)) throw new ArgumentException("Länken är ingen webbadress.");
        var path = PathOf(browser) ?? throw new IOException(browser.Name + " hittades inte på datorn.");
        var start = new ProcessStartInfo(path) { UseShellExecute = false };
        start.ArgumentList.Add(browser.Flag); start.ArgumentList.Add(uri.AbsoluteUri);
        using var _ = Process.Start(start);
    }

    private sealed record Choice(string Browser);
    public static Browser? Preferred
    {
        get
        {
            var installed = Installed; var path = Path.Combine(AppPaths.DataRoot, "private-browser.json");
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length < 256 && JsonSerializer.Deserialize<Choice>(File.ReadAllBytes(path)) is { } saved && installed.FirstOrDefault(b => b.Id == saved.Browser) is { } match) return match;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
            return installed.FirstOrDefault();
        }
        set
        {
            if (value is null) return;
            try { Directory.CreateDirectory(AppPaths.DataRoot); File.WriteAllText(FileCryptoPaths.Validate(Path.Combine(AppPaths.DataRoot, "private-browser.json")), JsonSerializer.Serialize(new Choice(value.Id))); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
