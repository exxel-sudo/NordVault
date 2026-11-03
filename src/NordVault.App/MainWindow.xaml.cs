using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using NordVault.Core;

namespace NordVault.App;

public partial class MainWindow : Window
{
    private readonly BackendClient _backend = new();
    private readonly bool _demo;
    private readonly bool _previewClosedVault;
    private readonly FileCryptoPanel _fileTools;
    private VaultPanel _vaultTools=null!;
    private PasswordsPanel _passwordTools=null!;
    private int _browserBusy;
    private readonly BrowserRateLimit _browserLimits=new();
    private VaultSession? _vaultSession;
    public bool HasOpenVault=>_vaultSession is not null;
    private readonly DispatcherTimer _idleTimer=new(){Interval=TimeSpan.FromSeconds(15)};
    private DateTimeOffset _lastInput=DateTimeOffset.UtcNow;
    public event Action? LockRequested;
    private bool AnyBusy=>_busy||_browserBusy>0||_vaultSession?.IsBusy==true||_fileTools?.IsBusy==true||_vaultTools?.IsBusy==true||_passwordTools?.IsBusy==true;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(20) };
    private DiscoveryResult _environment = new();
    private bool _busy;
    private bool _refreshing, _closing, _hasDiskStatus;
    private CancellationTokenSource? _refreshCancellation;
    private readonly Func<CancellationToken,Task<DiscoveryResult>> _readDiskStatus;
    private string ToolPath => Path.Combine(AppContext.BaseDirectory, "tools", "sdelete64.exe");
    private VolumeInfo? Selected => VolumeGrid.SelectedItem as VolumeInfo;

    public MainWindow(bool demo,VaultSession? vaultSession=null,bool previewClosedVault=false,Func<CancellationToken,Task<DiscoveryResult>>? readDiskStatus=null)
    {
        InitializeComponent();
        _readDiskStatus=readDiskStatus??(cancellation=>_backend.ExecuteAsync<DiscoveryResult>(new BackendRequest(),cancellation));
        _demo = demo;
        _previewClosedVault=previewClosedVault;
        _fileTools = new FileCryptoPanel(demo);
        SetVault(vaultSession);
        FileToolsHost.Content = _fileTools;
        _fileTools.BusyChanged += UpdateActions;
        _fileTools.ActivityChanged += message => ActivityText.Text = message;
        if (demo) LoadDemo();
        else Loaded += async (_, _) => await RefreshAsync();
        _timer.Tick += async (_, _) => { if (!AnyBusy && !OwnedWindows.Cast<Window>().Any()) await RefreshAsync(true); };
        Closing += OnClosing;
        System.Windows.Input.InputManager.Current.PreProcessInput+=OnInput;
        _idleTimer.Tick+=(_,_)=>
        {
            if(_demo||!HasOpenVault)return;
            if(ScreenSaverRunning()||DateTimeOffset.UtcNow-_lastInput>TimeSpan.FromMinutes(AppPaths.AutoLockMinutes)){LockNow();return;}
            if(AnyBusy||OwnedWindows.Cast<Window>().Any())return;
            if(_lockPending){_lockPending=false;RequestLock();}
        };
        if(!demo)
        {
            _idleTimer.Start();
            SystemEvents.SessionSwitch+=OnSessionSwitch;SystemEvents.PowerModeChanged+=OnPowerModeChanged;
        }
        UpdateActions();
    }

    private void LoadDemo()
    {
        _environment = new DiscoveryResult
        {
            IsAdministrator = true, BitLockerAvailable = true, TpmReady = true,
            Volumes = [
                new VolumeInfo { MountPoint="C:", VolumeId="demo-c", Label="Windows", VolumeType="OperatingSystem", DriveType="Fixed", FileSystem="NTFS", MediaKind="SSD", BusType="NVMe", SizeBytes=1000L*1073741824, VolumeStatus="FullyEncrypted", ProtectionStatus="On", LockStatus="Unlocked", EncryptionPercentage=100, EncryptionMethod="XtsAes256" },
                new VolumeInfo { MountPoint="D:", VolumeId="demo-d", Label="Privat arkiv", VolumeType="FixedData", DriveType="Fixed", FileSystem="NTFS", MediaKind="HDD", BusType="SATA", SizeBytes=2000L*1073741824, VolumeStatus="FullyDecrypted", ProtectionStatus="Off", LockStatus="Unlocked", EncryptionMethod="None" },
                new VolumeInfo { MountPoint="F:", VolumeId="demo-f", Label="USB · resor", VolumeType="RemovableData", DriveType="Removable", FileSystem="exFAT", MediaKind="Unknown", BusType="USB", SizeBytes=64L*1073741824, VolumeStatus="FullyDecrypted", ProtectionStatus="Off", LockStatus="Unlocked", EncryptionMethod="None" }
            ]
        };
        _environment = _environment with { Volumes = _environment.Volumes.Select(v => v with { WipingStatus = 0 }).ToArray() };
        VolumeGrid.ItemsSource = _environment.Volumes;
        VolumeGrid.SelectedIndex = 2;
        _hasDiskStatus=true;
        DiskReadText.Text="Simulerad diskstatus. Ingen avläsning pågår.";
        EnvironmentText.Text = "DEMO · tre simulerade volymer";
        EnvironmentDetail.Text = "Förhandsvisning av gränssnittet. Alla diskåtgärder är avstängda.";
    }

    private async Task RefreshAsync(bool quiet = false)
    {
        if (AnyBusy || _demo || _refreshing || _closing) return;
        _refreshing=true;
        _timer.Stop();
        using var cancellation=new CancellationTokenSource();
        _refreshCancellation=cancellation;
        var id = Selected?.VolumeId;
        DiskReadText.Text="Läser diskstatus i bakgrunden… Filkryptering och valv kan användas.";
        UpdateActions();
        try
        {
            var environment = await _readDiskStatus(cancellation.Token);
            if(_closing||cancellation.IsCancellationRequested)return;
            _environment=environment;
            _hasDiskStatus=true;
            VolumeGrid.ItemsSource = _environment.Volumes;
            VolumeGrid.SelectedItem = _environment.Volumes.FirstOrDefault(v => v.VolumeId == id) ?? _environment.Volumes.FirstOrDefault();
            EnvironmentText.Text = !_environment.IsAdministrator ? "Starta som administratör för att hantera diskar" : !_environment.BitLockerAvailable ? "BitLocker är inte tillgängligt i denna Windows-installation" : $"{_environment.Volumes.Count(v => v.IsProtected)} av {_environment.Volumes.Length} volymer har aktivt skydd";
            EnvironmentDetail.Text = !_environment.IsAdministrator ? "Diskhantering kräver administratör. Filkryptering kan användas direkt i fliken Filkryptering." : string.IsNullOrWhiteSpace(_environment.Message) ? "Välj disk eller USB. Återställningsnyckeln sparas före kryptering." : _environment.Message;
            DiskReadText.Text=$"Diskstatus uppdaterad {DateTime.Now:HH:mm:ss}."+(!_environment.IsAdministrator?" BitLocker-status kräver administratör.":"");
            if (!quiet&&!AnyBusy) ActivityText.Text = "Diskstatus uppdaterad. Inga diskar har ändrats.";
        }
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested) { }
        catch(Exception ex)
        {
            _hasDiskStatus=false;
            DiskReadText.Text=ex is InvalidOperationException?ex.Message:"Diskstatus kunde inte uppdateras. Försök igen.";
        }
        finally
        {
            _refreshing=false;
            _refreshCancellation=null;
            if(!_closing)
            {
                UpdateActions();
                // Count from completion so a slow query cannot cause back-to-back polling.
                _timer.Interval=TimeSpan.FromSeconds(_environment.IsAdministrator?20:60);
                _timer.Start();
            }
        }
    }

    private async Task RunAsync(Func<Task> operation, string activity, bool quiet = false)
    {
        if (AnyBusy || _demo) return;
        _busy = true;
        if (!quiet) ActivityText.Text = activity;
        BusyProgress.Visibility = Visibility.Visible;
        UpdateActions();
        try { await operation(); }
        catch (Exception ex)
        {
            ActivityText.Text = ex is InvalidOperationException ? ex.Message : "Åtgärden kunde inte bekräftas. Uppdatera diskstatus innan nästa försök.";
            if (!quiet) MessageBox.Show(this, ActivityText.Text, "NordVault", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _busy = false; BusyProgress.Visibility = Visibility.Collapsed; UpdateActions(); }
    }

    private void UpdateActions()
    {
        if (!IsInitialized) return;
        var v = Selected;
        var busy = AnyBusy;
        BusyProgress.Visibility=busy?Visibility.Visible:Visibility.Collapsed;
        _fileTools?.SetBlocked(_busy || _vaultTools?.IsBusy==true || _passwordTools?.IsBusy==true || _browserBusy>0 || OwnedWindows.OfType<RecoveryWindow>().Any());
        _vaultTools?.SetBlocked(_busy || _fileTools?.IsBusy==true || _passwordTools?.IsBusy==true || _browserBusy>0);
        _passwordTools?.SetBlocked(_busy || _fileTools?.IsBusy==true || _vaultTools?.IsBusy==true || _browserBusy>0);
        AppLockButton.IsEnabled=!busy&&!_demo&&HasOpenVault;
        AppLockButton.Content=AppPaths.RequireVaultAtStartup?"Lås appen":"Lås valvet";
        var ready = !busy && !_refreshing && _hasDiskStatus && !_demo && _environment.IsAdministrator && _environment.BitLockerAvailable;
        RefreshButton.IsEnabled = !busy && !_refreshing && !_demo;
        AdminButton.IsEnabled = !busy && !_demo && !_environment.IsAdministrator;
        EncryptButton.IsEnabled = ready && v is not null && SafetyPolicy.Encrypt(v, _environment).Allowed;
        UnlockButton.IsEnabled = ready && v is { IsSystem: false, LockStatus: "Locked" };
        LockButton.IsEnabled = ready && v is { IsSystem: false, LockStatus: "Unlocked", IsProtected: true };
        ProtectButton.IsEnabled = ready && v is { VolumeStatus: "FullyEncrypted", ProtectionStatus: "Off", LockStatus: "Unlocked" };
        WipeButton.IsEnabled = ready && v is not null && SafetyPolicy.WipeFreeSpace(v, _environment).Allowed;
        CancelWipeButton.IsEnabled = ready && v is { WipingStatus: 2 or 3 };
        ShredButton.IsEnabled = ready && _environment.Volumes.Any(x => SafetyPolicy.ShredFile(x, _environment).Allowed) && File.Exists(ToolPath);
        ToolButton.IsEnabled = !_busy;
        ToolDetail.Text = File.Exists(ToolPath) ? "SDelete finns installerat. Microsoft-signaturen kontrolleras inför varje filåtgärd." : "SDelete saknas. Kör scripts/Install-SDelete.ps1 för att hämta det från Microsoft och verifiera signaturen.";
        if (v is null)
        {
            SelectionTitle.Text = "Välj en disk i listan";
            SelectionDetail.Text = "Endast enheter som Windows kan identifiera visas här.";
            WipeDetail.Text = "Välj en volym i översikten.";
            return;
        }
        SelectionTitle.Text = v.DisplayName;
        var decision = SafetyPolicy.Encrypt(v, _environment);
        SelectionDetail.Text = $"{v.FileSystem} · {v.MediaText} · {v.StatusText}\n" + (decision.Allowed ? v.IsSystem ? "Systemdisk: TPM + PIN och återställningsnyckel. Windows gör starttest före kryptering." : "Data eller USB: lösenord och separat återställningsnyckel. Hela volymen krypteras." : decision.Reason);
        WipeDetail.Text = v.WipingStatus is 2 or 3 ? $"{v.MountPoint} · rensning {v.WipingPercentage}% ({(v.WipingStatus == 3 ? "pausad" : "pågår")})" : $"{v.MountPoint} · {SafetyPolicy.WipeFreeSpace(v, _environment).Reason}";
    }

    private BackendRequest Request(string action, VolumeInfo v) => new() { Action = action, MountPoint = v.MountPoint, VolumeId = v.VolumeId, Confirmed = true };
    private async Task ActionAsync(BackendRequest request, string activity)
    {
        await RunAsync(async () => { var result = await _backend.ExecuteAsync<OperationResult>(request); ActivityText.Text = result.Message; }, activity);
        await RefreshAsync(true);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Volume_Changed(object sender, SelectionChangedEventArgs e) => UpdateActions();
    private void NavEncryption_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 0;
    private void NavFiles_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 1;
    private void NavVault_Click(object sender,RoutedEventArgs e)=>MainTabs.SelectedIndex=2;
    private void NavPasswords_Click(object sender,RoutedEventArgs e)=>MainTabs.SelectedIndex=3;
    private void NavCleaning_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 4;
    private void NavRecovery_Click(object sender, RoutedEventArgs e) => MainTabs.SelectedIndex = 5;
    private void AppLock_Click(object sender,RoutedEventArgs e){if(!AnyBusy&&!_demo&&HasOpenVault)RequestLock();}
    private void RequestLock()
    {
        if(AppPaths.RequireVaultAtStartup){LockRequested?.Invoke();return;}
        SecureClipboard.ClearIfOurs();_fileTools.ClearSecrets();_vaultTools.Clear();_passwordTools.Clear();_vaultSession?.Dispose();SetVault(null);UpdateActions();
        ActivityText.Text="Valvet är låst. Diskar och filkryptering är fortfarande tillgängliga.";
    }
    private void SetVault(VaultSession? session)
    {
        _vaultSession=session;_vaultTools=new VaultPanel(session,_demo,_previewClosedVault);VaultToolsHost.Content=_vaultTools;
        _vaultTools.BusyChanged+=UpdateActions;
        _vaultTools.ActivityChanged+=message=>ActivityText.Text=message;
        _vaultTools.OpenRequested+=OpenVault;
        _vaultTools.LockRequired+=()=>{if(HasOpenVault&&!AnyBusy)RequestLock();};
        _passwordTools=new PasswordsPanel(session,_demo);PasswordToolsHost.Content=_passwordTools;
        _passwordTools.BusyChanged+=UpdateActions;
        _passwordTools.ActivityChanged+=message=>ActivityText.Text=message;
        _passwordTools.OpenRequested+=OpenVault;
        _passwordTools.LockRequired+=()=>{if(HasOpenVault&&!AnyBusy)RequestLock();};
        if(session is not null&&!_demo)_=ProcessInboxAsync(session);
    }

    /// <summary>Logins Firefox saved while the vault was locked: shown once after unlocking, added only if the user says yes.</summary>
    private async Task ProcessInboxAsync(VaultSession session)
    {
        if(!IsLoaded){RoutedEventHandler? later=null;later=(_,_)=>{Loaded-=later;_=ProcessInboxAsync(session);};Loaded+=later;return;}
        if(_vaultSession!=session)return;
        _browserBusy++;UpdateActions();
        InboxContents? inbox=null;
        try
        {
            var key=await session.PrepareInboxAsync();
            if(key is null||_vaultSession!=session)return;
            BrowserInbox.Trust(session.Root,key.PublicKey);
            if(key.Repaired)MessageBox.Show(this,"Brevlådans nyckel i valvmappen hade bytts ut av något annat än NordVault. Den är nu återställd.\n\nInloggningar som Firefox sparat sedan dess kan inte öppnas. Kontrollera datorn efter skadeprogram.","NordVault · varning om brevlådan",MessageBoxButton.OK,MessageBoxImage.Warning);
            inbox=await session.ReadInboxAsync();
            if(_vaultSession!=session||inbox.Logins.Count+inbox.Unreadable.Count==0)return;
            var broken=inbox.Unreadable.Count>0?$"{inbox.Unreadable.Count} fil{(inbox.Unreadable.Count==1?"":"er")} i brevlådan kunde inte öppnas och tas bort.\n\n":"";
            if(inbox.Logins.Count==0)
            {
                MessageBox.Show(this,broken.TrimEnd(),"NordVault · brevlådan",MessageBoxButton.OK,MessageBoxImage.Warning);
                session.DiscardInbox(inbox.Unreadable);return;
            }
            var lines=string.Join("\n",inbox.Logins.Take(12).Select(l=>"• "+(VaultItemMatcher.Parse(l.Url)?.IdnHost??l.Url)+(l.Username.Length>0?" · "+l.Username:" · (inget användarnamn)")));
            if(inbox.Logins.Count>12)lines+=$"\n… och {inbox.Logins.Count-12} till";
            var answer=MessageBox.Show(this,$"Firefox sparade {inbox.Logins.Count} inloggning{(inbox.Logins.Count==1?"":"ar")} krypterat medan valvet var låst:\n\n{lines}\n\n{broken}Ja = lägg in i valvet\nNej = ta bort dem\nAvbryt = fråga nästa gång",
                "NordVault · nya inloggningar från Firefox",MessageBoxButton.YesNoCancel,MessageBoxImage.Question,MessageBoxResult.Yes);
            if(_vaultSession!=session||answer==MessageBoxResult.Cancel)return;
            session.DiscardInbox(inbox.Unreadable);
            if(answer==MessageBoxResult.No){session.DiscardInbox(inbox.Logins.Select(l=>l.File));ActivityText.Text="Inloggningarna från Firefox togs bort.";return;}
            var saved=await session.AcceptInboxAsync(inbox.Logins);
            _passwordTools.Refresh();
            ActivityText.Text=$"{saved} inloggning{(saved==1?"":"ar")} från Firefox lades in i valvet.";
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException or ArgumentException)
        {ActivityText.Text="Brevlådan kunde inte läsas: "+error.Message;}
        finally{if(inbox is not null)foreach(var login in inbox.Logins)login.Dispose();_browserBusy--;UpdateActions();}
    }

    /// <summary>Requests from the Firefox extension (see BrowserBridge). Runs on the UI thread.</summary>
    internal async Task<System.Text.Json.Nodes.JsonObject> HandleBrowserAsync(System.Text.Json.Nodes.JsonObject request)
    {
        var type=request["type"]?.GetValue<string>();
        if(_demo||_closing)return BrowserBridge.Error("NordVault är inte redo.");
        if(type=="status")return new(){["ok"]=true,["unlocked"]=HasOpenVault};
        if(type=="open"){BringToFront();MainTabs.SelectedIndex=3;return new(){["ok"]=true};}
        if(type=="links")
        {
            // Bookmark search for the toolbar button: unlocked vault only, a search term is required and at most 15 hits are returned.
            if(_vaultSession is null)return BrowserBridge.Error("Valvet är låst.",true);
            if(!_browserLimits.Allow(type))return BrowserBridge.Error("För många sökningar. Vänta en minut.");
            var term=(request["query"]?.GetValue<string>()??"").Trim();
            if(term.Length is < 1 or > 100)return new(){["ok"]=true,["links"]=new System.Text.Json.Nodes.JsonArray()};
            var hits=new System.Text.Json.Nodes.JsonArray();
            foreach(var item in _vaultSession.Items.Where(i=>i.Kind==VaultItemKind.Link&&!i.Private&&i.Matches(term)&&VaultItemMatcher.Parse(i.Url) is not null).OrderBy(i=>i.Title,StringComparer.CurrentCultureIgnoreCase).Take(15))
                hits.Add(new System.Text.Json.Nodes.JsonObject{["title"]=item.Title,["url"]=VaultItemMatcher.Parse(item.Url)!.AbsoluteUri,["folder"]=item.Notes.StartsWith("Firefox-mapp: ",StringComparison.Ordinal)?item.Notes[14..].Split('\n')[0]:""});
            return new(){["ok"]=true,["links"]=hits};
        }
        var url=request["url"]?.GetValue<string>()??"";
        if(!url.StartsWith("https://",StringComparison.OrdinalIgnoreCase)&&!url.StartsWith("http://",StringComparison.OrdinalIgnoreCase)||VaultItemMatcher.Parse(url) is not {} page)
            return BrowserBridge.Error("Sidan har ingen webbadress som kan användas.");
        if(page.Scheme!="https"&&!VaultItemMatcher.IsLocalHost(page.IdnHost))
            return BrowserBridge.Error("NordVault sparar och fyller bara i på krypterade https-sidor.");
        if(!_browserLimits.Allow(type??""))return BrowserBridge.Error("För många förfrågningar från Firefox. Vänta en minut.");
        if(type=="query")
        {
            if(_vaultSession is null)return BrowserBridge.Error("Valvet är låst.",true);
            var logins=new System.Text.Json.Nodes.JsonArray();
            foreach(var item in _vaultSession.FindLogins(page))logins.Add(new System.Text.Json.Nodes.JsonObject{["id"]=item.Id,["title"]=item.Title,["username"]=item.Username});
            return new(){["ok"]=true,["logins"]=logins};
        }
        if(type is not ("save" or "fill"))return BrowserBridge.Error("Okänd begäran.");
        // Locked: a save goes to the vault's inbox without asking for the master password; it is added after the next unlock.
        if(type=="save"&&!HasOpenVault&&await BrowserInbox.TryQueueAsync(request) is {} queued)
        {
            if(queued["ok"]?.GetValue<bool>()==true)ActivityText.Text=$"Inloggningen för {page.IdnHost} sparades krypterat i brevlådan. Den läggs in när valvet låses upp.";
            return queued;
        }
        if(!EnsureUnlockedForBrowser())return BrowserBridge.Error("Lås upp valvet i NordVault och försök igen.",true);
        var session=_vaultSession!;
        _browserBusy++;UpdateActions();
        try
        {
            if(type=="save")
            {
                var username=request["username"]?.GetValue<string>()??"";var password=(request["password"]?.GetValue<string>()??"").ToCharArray();
                try
                {
                    var result=await session.SaveBrowserLoginAsync(page,username,password);
                    _passwordTools.Refresh();
                    ActivityText.Text=result switch{"created"=>$"Ny inloggning för {page.IdnHost} sparad krypterat.","updated"=>$"Lösenordet för {page.IdnHost} är uppdaterat.",_=>$"Lösenordet för {page.IdnHost} var redan sparat."};
                    return new(){["ok"]=true,["result"]=result};
                }
                finally{Array.Clear(password);}
            }
            var id=request["id"]?.GetValue<string>()??"";
            var login=session.FindLogins(page).FirstOrDefault(i=>i.Id==id);
            if(login is null)return BrowserBridge.Error("Inloggningen hör inte till den här webbplatsen.");
            BringToFront();
            if(session.HelloEnabled)
            {
                ActivityText.Text=$"Bekräfta ifyllningen för {page.IdnHost} med Windows Hello…";
                if(!await session.ConfirmWithHelloAsync(new System.Windows.Interop.WindowInteropHelper(this).Handle,$"Fyll i inloggningen ”{login.Title}” på {page.IdnHost} i Firefox")||_vaultSession!=session)
                    return BrowserBridge.Error("Ifyllningen godkändes inte.");
            }
            else
            {
            var answer=MessageBox.Show(this,$"Firefox vill fylla i inloggningen\n\n{login.Title}\nAnvändare: {(login.Username.Length>0?login.Username:"(inget)")}\nSida: {page.IdnHost}\n\nFyll i lösenordet på sidan?","NordVault · bekräfta ifyllning",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No);
            if(answer!=MessageBoxResult.Yes||_vaultSession!=session)return BrowserBridge.Error("Ifyllningen avbröts.");
            }
            var secret=session.ReleaseForConfirmedFill(login.Id,page);
            try{ActivityText.Text=$"Inloggningen för {page.IdnHost} fylldes i Firefox.";return new(){["ok"]=true,["username"]=login.Username,["password"]=new string(secret)};}
            finally{Array.Clear(secret);}
        }
        finally{_browserBusy--;UpdateActions();_lastInput=DateTimeOffset.UtcNow;}
    }
    private bool EnsureUnlockedForBrowser()
    {
        if(HasOpenVault)return true;
        if(AnyBusy||OwnedWindows.Cast<Window>().Any())return false;
        BringToFront();MainTabs.SelectedIndex=3;OpenVault();
        return HasOpenVault;
    }
    private void BringToFront()
    {
        if(!IsVisible)Show();if(WindowState==WindowState.Minimized)WindowState=WindowState.Normal;
        Activate();Topmost=true;Topmost=false;Focus();
    }
    private void OpenVault()
    {
        if(AnyBusy||_demo||HasOpenVault)return;
        var gate=new VaultGateWindow{Owner=this};if(gate.ShowDialog()!=true||gate.Session is null)return;
        SetVault(gate.Session);_lastInput=DateTimeOffset.UtcNow;UpdateActions();
        ActivityText.Text="Valvet är upplåst. Ingen disk har ändrats.";
    }
    private void OnInput(object sender,System.Windows.Input.PreProcessInputEventArgs e)=>_lastInput=DateTimeOffset.UtcNow;

    // Windows locked, user switched, remote session disconnected, sleep or screen saver: lock the vault at once.
    private bool _lockPending;
    private void OnSessionSwitch(object? sender,SessionSwitchEventArgs e){if(e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)Dispatcher.BeginInvoke(LockNow);}
    private void OnPowerModeChanged(object? sender,PowerModeChangedEventArgs e){if(e.Mode==PowerModes.Suspend)Dispatcher.BeginInvoke(LockNow);}
    private void LockNow()
    {
        if(_demo||_closing||!HasOpenVault)return;
        // Windows that show or take secrets close; long operations finish first and lock right after.
        foreach(var window in OwnedWindows.Cast<Window>().ToArray())if(window is RevealWindow or ItemEditorWindow or SecretWindow or FirefoxImportWindow or FirefoxCleanupWindow or PrivateLinksWindow or BulkLinksWindow)window.Close();
        SecureClipboard.ClearIfOurs();
        if(AnyBusy||OwnedWindows.Cast<Window>().Any()){_lockPending=true;ActivityText.Text="Valvet låses så snart pågående åtgärd är klar.";return;}
        RequestLock();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll",SetLastError=true)] private static extern bool SystemParametersInfo(uint action,uint param,out bool result,uint winIni);
    private static bool ScreenSaverRunning()=>SystemParametersInfo(0x0072,0,out var running,0)&&running;

    private void Encrypt_Click(object sender, RoutedEventArgs e)
    {
        if (!EncryptButton.IsEnabled || Selected is not { } volume || !SafetyPolicy.Encrypt(volume, _environment).Allowed) return;
        var dialog = new RecoveryWindow(_backend, volume) { Owner = this };
        dialog.ShowDialog();
        _ = RefreshAsync();
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e)
    {
        if (!UnlockButton.IsEnabled || Selected is not { } volume) return;
        var dialog = new SecretWindow("Lås upp " + volume.MountPoint, "Ange lösenord eller den 48-siffriga återställningsnyckeln.") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var secret = dialog.Secret;
        dialog.Clear();
        await ActionAsync(Request("Unlock", volume) with { Password = secret }, "Låser upp volym…");
    }

    private async void Lock_Click(object sender, RoutedEventArgs e)
    {
        if (!LockButton.IsEnabled || Selected is not { } volume) return;
        if (!Confirm(volume, "Lås volymen. Stäng först öppna filer på enheten. Ingen tvångsavmontering används.")) return;
        await ActionAsync(Request("Lock", volume), "Låser volym…");
    }

    private async void Protect_Click(object sender, RoutedEventArgs e)
    {
        if (!ProtectButton.IsEnabled || Selected is not { } volume) return;
        if (!Confirm(volume, "Återaktivera nyckelskyddet för den krypterade volymen.")) return;
        await ActionAsync(Request("ResumeProtection", volume), "Återaktiverar skydd…");
    }

    private async void Wipe_Click(object sender, RoutedEventArgs e)
    {
        if (!WipeButton.IsEnabled || Selected is not { } volume) return;
        if (!Confirm(volume, "Rensa ledigt utrymme efter raderade filer. Kan ta lång tid. Befintliga filer behålls.\n\nSSD/USB-flash: reservceller kan innehålla tidigare data även efter rensning.")) return;
        await ActionAsync(Request("WipeFreeSpace", volume), "Startar rensning av ledigt utrymme…");
    }

    private async void CancelWipe_Click(object sender, RoutedEventArgs e)
    {
        if (!CancelWipeButton.IsEnabled || Selected is not { } volume) return;
        if (!Confirm(volume, "Avbryt rensningen av ledigt utrymme. Redan rensat utrymme förblir rensat.")) return;
        await ActionAsync(Request("CancelWipe", volume), "Avbryter rensning…");
    }

    private async void Shred_Click(object sender, RoutedEventArgs e)
    {
        if (!ShredButton.IsEnabled) return;
        var picker = new OpenFileDialog { Title = "Välj en fil på en identifierad HDD", Multiselect = false, CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        var root = Path.GetPathRoot(picker.FileName)?.TrimEnd('\\');
        var volume = _environment.Volumes.FirstOrDefault(v => string.Equals(v.MountPoint, root, StringComparison.OrdinalIgnoreCase));
        if (volume is null || !SafetyPolicy.ShredFile(volume, _environment).Allowed)
        {
            MessageBox.Show(this, volume is null ? "Filens volym kunde inte identifieras. Uppdatera diskstatus." : SafetyPolicy.ShredFile(volume, _environment).Reason, "Filförstöring", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var confirm = new ConfirmationWindow("Förstör en fil", picker.FileName + "\n\nFilen skrivs över och tas bort permanent. Kopior och säkerhetskopior påverkas inte. Skriv FÖRSTÖR för att fortsätta.", "FÖRSTÖR", true) { Owner = this };
        if (confirm.ShowDialog() != true) return;
        await ActionAsync(Request("ShredFile", volume) with { FilePath = picker.FileName, ToolPath = ToolPath, EulaAccepted = true }, "Förstör vald fil. Vänta tills resultatet har bekräftats…");
    }

    private bool Confirm(VolumeInfo volume, string text)
    {
        var dialog = new ConfirmationWindow(volume.DisplayName, text + $"\n\nSkriv {volume.MountPoint} för att bekräfta vald volym.", volume.MountPoint) { Owner = this };
        return dialog.ShowDialog() == true;
    }

    private void Admin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "NordVault.exe")) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory });
            Close();
        }
        catch (Win32Exception) { ActivityText.Text = "Administratörsstarten avbröts. Diskar har inte ändrats."; }
    }

    private void Tool_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://learn.microsoft.com/en-us/sysinternals/downloads/sdelete") { UseShellExecute = true });
    private void Docs_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "README.sv.md");
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else MessageBox.Show(this, "Projektets README.sv.md innehåller bygg- och användarinstruktioner.", "NordVault");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AnyBusy) { e.Cancel = true; ActivityText.Text = "Avbryt fil- eller valvåtgärden, eller vänta tills Windows har svarat innan du stänger."; }
        else {_closing=true;_refreshCancellation?.Cancel();_timer.Stop();_idleTimer.Stop();SystemEvents.SessionSwitch-=OnSessionSwitch;SystemEvents.PowerModeChanged-=OnPowerModeChanged;System.Windows.Input.InputManager.Current.PreProcessInput-=OnInput;SecureClipboard.ClearIfOurs();_fileTools.ClearSecrets();_vaultTools.Clear();_passwordTools.Clear();_vaultSession?.Dispose();}
    }
}
