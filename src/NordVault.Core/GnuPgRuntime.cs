using System.Security.Cryptography;
using System.Text.Json;

namespace NordVault.Core;

public sealed class GnuPgRuntime
{
    private readonly string _root;
    public string GpgPath => Path.Combine(_root, "bin", "gpg.exe");
    public string AgentPath => Path.Combine(_root, "bin", "gpg-agent.exe");
    public string GpgConfPath => Path.Combine(_root, "bin", "gpgconf.exe");
    public string BinPath => Path.Combine(_root, "bin");
    public bool IsInstalled => File.Exists(GpgPath);

    public GnuPgRuntime(string root) => _root = Path.GetFullPath(root);

    public void Verify()
    {
        if (!IsInstalled) throw new InvalidOperationException("GnuPG-filmotorn saknas. Kör scripts/Install-GnuPG.ps1 och bygg programmet igen.");
        using var manifest = typeof(GnuPgRuntime).Assembly.GetManifestResourceStream("NordVault.Core.GnuPgManifest.json")
            ?? throw new InvalidOperationException("Filmotorernas kontrollsummor saknas i programmet.");
        var files = JsonSerializer.Deserialize<Dictionary<string, string>>(manifest)
            ?? throw new InvalidOperationException("Filmotorernas kontrollsummor kunde inte läsas.");
        foreach (var (relative, expected) in files)
        {
            var file = Path.Combine(_root, relative);
            FileCryptoPaths.AssertNoReparsePoints(file);
            using var stream = File.OpenRead(file);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("GnuPG eller dess bibliotek har ändrats. Filåtgärder är blockerade tills en verifierad motor återställs.");
        }
        foreach (var file in Directory.EnumerateFiles(BinPath))
            if (!files.ContainsKey(Path.Combine("bin", Path.GetFileName(file))))
                throw new InvalidOperationException("En okänd fil finns i GnuPG:s programkatalog. Filåtgärder är blockerade.");
        if (Directory.EnumerateDirectories(BinPath).Any())
            throw new InvalidOperationException("En okänd undermapp finns i GnuPG:s programkatalog. Filåtgärder är blockerade.");
    }
}
