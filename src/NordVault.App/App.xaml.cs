using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NordVault.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if(e.Args.Contains(BrowserBridge.ExtensionId))
        {
            // Started by Firefox as native-messaging host: no window, only a relay to the running NordVault.
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            int code;try{code=await NativeHost.RunAsync();}catch(Exception){code=1;}
            Shutdown(code);return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("Åtgärden avbröts. Kontrollera diskstatus innan du försöker igen.", "NordVault", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        // Started from the Explorer right-click menu: only that window, no vault and no Firefox relay.
        foreach(var (argument,open) in new (string,Func<string,Window>)[]{(NordVault.Core.ExplorerMenu.ArchiveArgument,p=>new ArchiveWindow(p)),(NordVault.Core.ExplorerMenu.WipeArgument,p=>new WipeFreeSpaceWindow(p))})
        {
            var at=Array.IndexOf(e.Args,argument);if(at<0)continue;
            var target=at+1<e.Args.Length?e.Args[at+1].TrimEnd('"'):"";if(target.Length==2&&target[1]==':')target+="\\";
            if(!Path.IsPathFullyQualified(target)||!Path.Exists(target)){MessageBox.Show("Hittar inte: "+target,"NordVault",MessageBoxButton.OK,MessageBoxImage.Warning);Shutdown(1);return;}
            MainWindow=open(target);MainWindow.Show();return;
        }
        var renderIndex = Array.IndexOf(e.Args, "--render-preview");
        var demo=e.Args.Contains("--demo")||renderIndex>=0;
        var window = demo?new MainWindow(true,previewClosedVault:e.Args.Contains("--preview-closed-vault")):null;
        if (renderIndex >= 0 && renderIndex + 1 < e.Args.Length)
        {
            var path = Path.GetFullPath(e.Args[renderIndex + 1]);
            var tabIndex = Array.IndexOf(e.Args, "--preview-tab");
            if(e.Args.Contains("--preview-gate"))window=null;
            Window preview=window is null?new VaultGateWindow(true):window;
            var dialog=e.Args.Contains("--preview-archive")?new ArchiveWindow(@"C:\Users\Anna\Bilder\Semester 2026",true):e.Args.Contains("--preview-wipe")?(Window)new WipeFreeSpaceWindow(@"F:\",true):null;
            if (tabIndex >= 0 && tabIndex + 1 < e.Args.Length && int.TryParse(e.Args[tabIndex + 1], out var selectedTab) && selectedTab is >= 0 and <= 5&&window is not null)
                ((System.Windows.Controls.TabControl)window.FindName("MainTabs")).SelectedIndex = selectedTab;
            var visual = (FrameworkElement)(dialog??preview).Content;
            // Small windows size to their content: measure at their own width.
            var size=dialog is null?new Size(1200,820):new Size(dialog.Width,double.PositiveInfinity);
            visual.Measure(size);if(dialog is not null)size=new Size(dialog.Width,Math.Ceiling(visual.DesiredSize.Height));
            visual.Arrange(new Rect(size));
            visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
            Shutdown();
            return;
        }
        if(demo){MainWindow=window;window!.Show();return;}
        ShutdownMode=ShutdownMode.OnExplicitShutdown;
        var refreshTest=Array.IndexOf(e.Args,"--test-refresh");
        if(refreshTest>=0&&refreshTest+1<e.Args.Length)
        {
            try{var result=await NordVault.App.MainWindow.RunDiskRefreshTestsAsync();File.WriteAllText(e.Args[refreshTest+1],System.Text.Json.JsonSerializer.Serialize(result));Shutdown(0);}
            catch(Exception error){File.WriteAllText(e.Args[refreshTest+1],System.Text.Json.JsonSerializer.Serialize(new{Success=false,Error=error.Message}));Shutdown(1);}
            return;
        }
        var test=Array.IndexOf(e.Args,"--test-startup");
        if(test>=0&&test+1<e.Args.Length)
        {
            if(AppPaths.RequireVaultAtStartup)throw new InvalidOperationException("Startup test requires the default profile without forced vault login.");
            var catalog=Path.Combine(AppPaths.PreferredVault,"catalog.gpg");var existed=File.Exists(catalog);
            var normal=new MainWindow(false);MainWindow=normal;
            var created=!existed&&File.Exists(catalog);
            var result=new{Success=!normal.HasOpenVault&&!created,MainWindow=normal.GetType().Name,VaultCreated=created};
            File.WriteAllText(e.Args[test+1],System.Text.Json.JsonSerializer.Serialize(result));normal.Close();Shutdown(result.Success?0:1);return;
        }
        _browser=new BrowserBridge.Server(HandleBrowserAsync);_browser.Start();Exit+=(_,_)=>_browser.Dispose();
        CheckFirefoxRegistration();
        try{NordVault.Core.ExplorerMenu.Ensure(Environment.ProcessPath!);}catch(Exception error)when(error is UnauthorizedAccessException or System.Security.SecurityException or IOException){}
        OpenApp();
    }
    private BrowserBridge.Server? _browser;
    /// <summary>Every start: warn if something else changed which program Firefox starts for NordVault.</summary>
    private static void CheckFirefoxRegistration()
    {
        if(FirefoxRegistration.Check()!=FirefoxRegistration.State.Changed)return;
        var answer=MessageBox.Show("Kopplingen mellan Firefox och NordVault har ändrats av något annat program, eller pekar på en annan NordVault-mapp.\n\n"+
            "Firefox kan då starta ett annat program när tillägget används. Om du inte själv har flyttat NordVault eller kör flera kopior bör du kontrollera datorn efter skadeprogram.\n\n"+
            "Ja = återställ kopplingen till den här NordVault\nNej = ta bort kopplingen (tillägget slutar fungera)\nAvbryt = gör ingenting nu",
            "NordVault · varning om Firefox-kopplingen",MessageBoxButton.YesNoCancel,MessageBoxImage.Warning,MessageBoxResult.Cancel);
        try
        {
            if(answer==MessageBoxResult.Yes){FirefoxRegistration.Unregister();FirefoxRegistration.Register();}
            else if(answer==MessageBoxResult.No)FirefoxRegistration.Unregister();
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {MessageBox.Show("Kopplingen kunde inte ändras: "+error.Message+"\nEn maskinövergripande post (HKLM) kräver administratör att ta bort.","NordVault",MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private async Task<System.Text.Json.Nodes.JsonObject> HandleBrowserAsync(System.Text.Json.Nodes.JsonObject request)
    {
        var type=request["type"]?.GetValue<string>();
        // Locked behind the startup vault prompt: a save goes straight to the vault's inbox, when it has one.
        if(type=="save"&&MainWindow is not NordVault.App.MainWindow&&await BrowserInbox.TryQueueAsync(request) is {} queued)return queued;
        // While the startup vault prompt is open there is no main window yet: user-started actions wait for it.
        for(var waited=0;MainWindow is not NordVault.App.MainWindow&&type is "save" or "fill" or "open"&&waited<240;waited++)await Task.Delay(500);
        if(MainWindow is NordVault.App.MainWindow window)return await window.HandleBrowserAsync(request);
        return type=="status"?new(){["ok"]=true,["unlocked"]=false}:BrowserBridge.Error("Lås upp valvet i NordVault först.",true);
    }
    private void OpenApp()
    {
        NordVault.Core.VaultSession? session=null;
        if(AppPaths.RequireVaultAtStartup)
        {var gate=new VaultGateWindow();if(gate.ShowDialog()!=true||gate.Session is null){Shutdown();return;}session=gate.Session;}
        var window=new MainWindow(false,session);MainWindow=window;
        var locking=false;
        window.LockRequested+=()=>{locking=true;window.Hide();window.Close();};
        window.Closed+=(_,_)=>{if(locking)Dispatcher.BeginInvoke((Action)OpenApp);else Shutdown();};
        window.Show();
    }
}
