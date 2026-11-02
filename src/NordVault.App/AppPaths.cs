using System.IO;
using System.Text.Json;
using NordVault.Core;

namespace NordVault.App;
internal static class AppPaths
{
    public static bool RequireVaultAtStartup
    {
        get
        {
            var path=Path.Combine(DataRoot,"startup.json");
            try {if(File.Exists(path)&&new FileInfo(path).Length<2048)return JsonSerializer.Deserialize<StartupSettings>(File.ReadAllBytes(path))?.RequireVaultAtStartup==true;}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException) { }
            return false;
        }
    }
    public static void SetRequireVaultAtStartup(bool required)
    {
        Directory.CreateDirectory(DataRoot);FileCryptoPaths.AssertNoReparsePoints(DataRoot);
        var path=FileCryptoPaths.Validate(Path.Combine(DataRoot,"startup.json"));
        File.WriteAllText(path,JsonSerializer.Serialize(new StartupSettings(required)));
    }
    private sealed record StartupSettings(bool RequireVaultAtStartup);
    public static readonly int[] AutoLockChoices=[2,5,10];
    /// <summary>Minutes without input before the open vault locks. Default 10.</summary>
    public static int AutoLockMinutes
    {
        get
        {
            var path=Path.Combine(DataRoot,"autolock.json");
            try{if(File.Exists(path)&&new FileInfo(path).Length<256&&JsonSerializer.Deserialize<AutoLockSettings>(File.ReadAllBytes(path)) is {} s&&AutoLockChoices.Contains(s.Minutes))return s.Minutes;}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException){}
            return 10;
        }
    }
    public static void SetAutoLockMinutes(int minutes)
    {
        if(!AutoLockChoices.Contains(minutes))throw new ArgumentOutOfRangeException(nameof(minutes));
        Directory.CreateDirectory(DataRoot);FileCryptoPaths.AssertNoReparsePoints(DataRoot);
        File.WriteAllText(FileCryptoPaths.Validate(Path.Combine(DataRoot,"autolock.json")),JsonSerializer.Serialize(new AutoLockSettings(minutes)));
    }
    private sealed record AutoLockSettings(int Minutes);

    // Windows Hello unlock is offered only after the master password (or a recovery code) has been used since the computer started.
    private static DateTimeOffset BootTime=>DateTimeOffset.UtcNow-TimeSpan.FromMilliseconds(Environment.TickCount64);
    private sealed record HelloBoot(string Root,DateTimeOffset BootUtc);
    public static void RememberMasterUnlock(string root)
    {
        try{Directory.CreateDirectory(DataRoot);FileCryptoPaths.AssertNoReparsePoints(DataRoot);File.WriteAllText(FileCryptoPaths.Validate(Path.Combine(DataRoot,"hello-boot.json")),JsonSerializer.Serialize(new HelloBoot(Path.GetFullPath(root),BootTime)));}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
    }
    public static bool HelloAllowedSinceBoot(string root)
    {
        var path=Path.Combine(DataRoot,"hello-boot.json");
        try
        {
            if(!File.Exists(path)||new FileInfo(path).Length>2048||JsonSerializer.Deserialize<HelloBoot>(File.ReadAllBytes(path)) is not {} saved)return false;
            return string.Equals(saved.Root,Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase)&&(saved.BootUtc-BootTime).Duration()<TimeSpan.FromMinutes(2);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException or ArgumentException){return false;}
    }
    public static string DataRoot
    {
        get
        {
            var marker=Path.Combine(AppContext.BaseDirectory,DeploymentService.StateFile);
            if(File.Exists(marker)&&new FileInfo(marker).Length<8192)
            {
                try {var state=JsonSerializer.Deserialize<InstallationState>(File.ReadAllBytes(marker));if(state is {Product:DeploymentService.ProductId,Mode:"User" or "AllUsers"})return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"NordVault","Data");}
                catch(JsonException) { }
            }
            return Path.Combine(AppContext.BaseDirectory,"Data");
        }
    }
    public static string PreferredVault
    {
        get
        {
            var config=Path.Combine(DataRoot,"vault-location.txt");
            if(File.Exists(config)&&new FileInfo(config).Length<1024)
            {try {var value=File.ReadAllText(config).Trim();return FileCryptoPaths.Validate(Path.IsPathFullyQualified(value)?value:Path.GetFullPath(Path.Combine(DataRoot,value)));}catch(Exception ex)when(ex is IOException or ArgumentException or UnauthorizedAccessException) { }}
            return Path.Combine(DataRoot,"Vault");
        }
    }
    public static void Remember(string root)
    {
        Directory.CreateDirectory(DataRoot);FileCryptoPaths.AssertNoReparsePoints(DataRoot);
        var value=root.StartsWith(DataRoot.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)?Path.GetRelativePath(DataRoot,root):root;
        File.WriteAllText(Path.Combine(DataRoot,"vault-location.txt"),value);
    }
}
