using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NordVault.Core;

namespace NordVault.Setup;

public sealed class App : Application
{
    [STAThread] public static void Main()=>new App().Run();
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var wait=Array.IndexOf(e.Args,"--wait-for");
        if(wait>=0&&wait+1<e.Args.Length&&int.TryParse(e.Args[wait+1],out var parentId)&&parentId>0&&parentId!=Environment.ProcessId)
        {
            try{using var parent=System.Diagnostics.Process.GetProcessById(parentId);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));await parent.WaitForExitAsync(timeout.Token);}
            catch(ArgumentException) { /* The previous installer already exited. */ }
            catch(OperationCanceledException){MessageBox.Show("Stäng det föregående installationsprogrammet och försök igen.","NordVault");Shutdown(1);return;}
        }
        var test=Array.IndexOf(e.Args,"--test-install");
        if(test>=0&&test+1<e.Args.Length)
        {
            try
            {
                var root=FileCryptoPaths.Validate(e.Args[test+1]);
                if(!Directory.Exists(root)||!Path.GetFileName(root).StartsWith("NV-",StringComparison.Ordinal)||Directory.EnumerateFileSystemEntries(root).Any())throw new IOException("Test requires a new, empty NV-* workspace.");
                var service=new DeploymentService(PackageResources.Manifest());var folder=Path.Combine(root,"program");
                using(var payload=PackageResources.Payload())await service.InstallAsync(payload,folder,"Portable",Environment.ProcessPath!);
                var app=Path.Combine(folder,"NordVault.exe");var setup=Path.Combine(folder,"NordVault.Setup.exe");
                WindowsShortcut.Create(Path.Combine(root,"NordVault.lnk"),app);
                WindowsShortcut.Create(Path.Combine(root,"Avinstallera NordVault.lnk"),setup,"--uninstall \""+folder+"\"");
                var link=WindowsShortcut.Read(Path.Combine(root,"Avinstallera NordVault.lnk"));
                if(link.Target!=setup||!link.Arguments.Contains("--uninstall",StringComparison.Ordinal))throw new IOException("Uninstall shortcut failed verification.");
                var data=Path.Combine(folder,"Data");Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"keep.gpg"),"test ciphertext");
                var result=service.Uninstall(folder,Environment.ProcessPath!);
                if(File.Exists(app)||!File.Exists(Path.Combine(data,"keep.gpg"))||result.PreservedFiles!=0)throw new IOException("Published installer did not preserve test data.");
                File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new{Success=true,Version=PackageResources.Version,ShortcutsVerified=true,DataPreserved=true}));Shutdown(0);
            }
            catch(Exception error)
            {File.WriteAllText(Path.Combine(e.Args[test+1],"result.json"),JsonSerializer.Serialize(new{Success=false,Error=error.GetType().Name,Message=error.Message}));Shutdown(1);}
            return;
        }
        var window=new SetupWindow(e.Args);MainWindow=window;
        var preview=Array.IndexOf(e.Args,"--render-preview");
        if(preview>=0&&preview+1<e.Args.Length)
        {
            var visual=(FrameworkElement)window.Content;visual.Measure(new Size(940,760));visual.Arrange(new Rect(0,0,940,760));visual.UpdateLayout();
            var bitmap=new RenderTargetBitmap(940,760,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(e.Args[preview+1]);encoder.Save(file);Shutdown();return;
        }
        window.Show();
    }
}

internal static class PackageResources
{
    public const string Version="0.4.3";
    public const string LegacySetupHash="EAD9101F9F9BF150191DE827B58AF0433114D5A0EA385203B27F4244110DEEF7";
    public const string Legacy031SetupHash="2B6A18EBD88EF62832BC47A85E316E43AD9B918B77FE33A6644D723540552632";
    public const string Legacy032SetupHash="9B7F169A8A75649EFACD6F7B1D89344243E6EA6A0942849DBE957E4CE21D4092";
    public const string Legacy040SetupHash="56C586210FA23326291CA8756DD7E44ACC16C808557681D18F6D5CBCB6649013";
    public const string Legacy041SetupHash="B1078E359918CCE2E3F326E2F66AD312832ADC5AB1B737FE8DB0072C55BF0591";
    public const string Legacy042SetupHash="8AFE13D85E2DB604362B87E1DCAB829ADFC26ACA45E7C6C6B267008145D131A6";
    public static Stream Payload()=>Assembly.GetExecutingAssembly().GetManifestResourceStream("NordVault.Setup.Payload.zip")??throw new IOException("Installationspaketet saknas.");
    public static DeploymentManifest Manifest()=>Read("NordVault.Setup.Manifest.json");
    private static DeploymentManifest Read(string name)
    {using var input=Assembly.GetExecutingAssembly().GetManifestResourceStream(name)??throw new IOException("Installationsmanifestet saknas.");using var memory=new MemoryStream();input.CopyTo(memory);return DeploymentService.ReadManifest(memory.ToArray());}
    public static (DeploymentManifest Manifest,string SetupHash) Previous(InstallationState state)
    {
        if(state.Version==Version)return(Manifest(),FileHash(Environment.ProcessPath!));
        if(state.Version=="0.3.0"&&state.SetupSha256==LegacySetupHash)return(Read("NordVault.Setup.Legacy030.json"),LegacySetupHash);
        if(state.Version=="0.3.1"&&state.SetupSha256==Legacy031SetupHash)return(Read("NordVault.Setup.Legacy031.json"),Legacy031SetupHash);
        if(state.Version=="0.3.2"&&state.SetupSha256==Legacy032SetupHash)return(Read("NordVault.Setup.Legacy032.json"),Legacy032SetupHash);
        if(state.Version=="0.4.0"&&state.SetupSha256==Legacy040SetupHash)return(Read("NordVault.Setup.Legacy040.json"),Legacy040SetupHash);
        if(state.Version=="0.4.1"&&state.SetupSha256==Legacy041SetupHash)return(Read("NordVault.Setup.Legacy041.json"),Legacy041SetupHash);
        if(state.Version=="0.4.2"&&state.SetupSha256==Legacy042SetupHash)return(Read("NordVault.Setup.Legacy042.json"),Legacy042SetupHash);
        throw new IOException("Den här äldre byggversionen saknar ett verifierat uppdateringsmanifest. Använd dess egen avinstallerare eller välj en ny mapp.");
    }
    public static string FileHash(string path){using var input=File.OpenRead(path);return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));}
}
