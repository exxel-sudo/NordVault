using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using NordVault.Core;

namespace NordVault.App;

internal sealed class ItemEditorWindow : Window
{
    private readonly ComboBox _kind=new(){Margin=new Thickness(0,5,0,12)};
    private readonly TextBox _title=new();private readonly TextBox _url=new();private readonly TextBox _username=new();
    private readonly ComboBox _linked=new(){IsEditable=true,Margin=new Thickness(0,5,0,12)};
    private readonly PasswordBox _password=new();private readonly TextBox _visible=new(){IsReadOnly=true,Visibility=Visibility.Collapsed};
    private readonly CheckBox _show=new(){Content="Visa det jag skriver här",Margin=new Thickness(0,0,0,8)};
    private readonly CheckBox _removePassword=new(){Content="Ta bort sparat lösenord",Margin=new Thickness(0,0,0,8),Visibility=Visibility.Collapsed};
    private readonly Button _generate=new(){Content="Generera starkt lösenord",HorizontalAlignment=HorizontalAlignment.Left};
    private readonly TextBox _notes=new(){AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MinHeight=110,MaxHeight=260,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    private readonly TextBlock _error=new(){Foreground=System.Windows.Media.Brushes.Firebrick,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)};
    private readonly StackPanel _urlRow=new(),_userRow=new(),_linkedRow=new(),_passwordRow=new();
    private readonly TextBlock _passwordHint=new(){FontSize=12,TextWrapping=TextWrapping.Wrap,Foreground=System.Windows.Media.Brushes.DimGray};
    private readonly VaultItem? _existing;
    public VaultItemDraft? Draft{get;private set;}
    /// <summary>The user chose to paste many links instead; the caller opens <see cref="BulkLinksWindow"/>.</summary>
    public bool BulkRequested{get;private set;}
    private readonly Button _bulk=new(){Content="Lägg till många länkar på en gång…",HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,0,0,14)};
    public bool ReplacePassword{get;private set;}
    private char[] _taken=[];
    /// <summary>Caller clears the result.</summary>
    public char[] TakePassword(){var result=_taken;_taken=[];return result;}

    private readonly bool _private;
    /// <param name="isPrivate">Privat section: links (with notes) only.</param>
    public ItemEditorWindow(VaultItem? existing,VaultItemKind kind,IEnumerable<string> logins,bool isPrivate=false)
    {
        _private=isPrivate;
        _existing=existing;Title=existing is null?"Ny post":"Redigera ”"+existing.Title+"”";Width=560;Height=760;MinHeight=480;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new Thickness(26)};
        panel.Children.Add(new TextBlock{Text=Title,FontSize=23,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,12)});
        foreach(var k in isPrivate?[VaultItemKind.Link]:Enum.GetValues<VaultItemKind>())_kind.Items.Add(new ComboBoxItem{Content=new VaultItem("",k,"","","","","",false,default,default).KindText+(isPrivate?" (privat)":""),Tag=k});
        _kind.IsEnabled=!isPrivate;
        panel.Children.Add(new TextBlock{Text="Typ"});panel.Children.Add(_kind);
        if(existing is null)panel.Children.Add(_bulk);
        panel.Children.Add(new TextBlock{Text="Titel"});panel.Children.Add(_title);
        _urlRow.Children.Add(new TextBlock{Text="Webbadress (t.ex. https://exempel.se)"});_urlRow.Children.Add(_url);panel.Children.Add(_urlRow);
        _userRow.Children.Add(new TextBlock{Text="Användarnamn eller e-post"});_userRow.Children.Add(_username);panel.Children.Add(_userRow);
        foreach(var login in logins)_linked.Items.Add(login);foreach(var common in new[]{"Google-kontot","Microsoft-kontot","Apple-ID","Facebook","BankID"})if(!_linked.Items.Contains(common))_linked.Items.Add(common);
        _linkedRow.Children.Add(new TextBlock{Text="Loggar in via (kopplat konto)"});_linkedRow.Children.Add(_linked);panel.Children.Add(_linkedRow);
        _passwordRow.Children.Add(new TextBlock{Text="Lösenord"});_passwordRow.Children.Add(_password);_passwordRow.Children.Add(_visible);_passwordRow.Children.Add(_passwordHint);
        _passwordRow.Children.Add(_generate);_passwordRow.Children.Add(_show);_passwordRow.Children.Add(_removePassword);panel.Children.Add(_passwordRow);
        panel.Children.Add(new TextBlock{Text="Anteckning / text"});panel.Children.Add(_notes);
        panel.Children.Add(_error);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var save=new Button{Content="Spara krypterat",Style=(Style)Application.Current.FindResource("Primary")};buttons.Children.Add(new Button{Content="Avbryt",IsCancel=true});buttons.Children.Add(save);panel.Children.Add(buttons);
        Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        _kind.SelectedIndex=isPrivate?0:(int)kind;
        if(existing is not null){_title.Text=existing.Title;_url.Text=existing.Url;_username.Text=existing.Username;_linked.Text=existing.LinkedAccount;_notes.Text=existing.Notes;_removePassword.Visibility=existing.HasPassword?Visibility.Visible:Visibility.Collapsed;}
        _passwordHint.Text=existing?.MustChange==true?"⚠ Det här lösenordet låg i Firefox och kan ha läckt. Byt det på webbplatsen och skriv in det nya här, så försvinner markeringen.":
            existing?.HasPassword==true?"Ett lösenord är redan sparat. Lämna fältet tomt för att behålla det.":"Valfritt för konton som loggar in via ett annat konto.";
        _kind.SelectionChanged+=(_,_)=>Layout();_show.Checked+=(_,_)=>ShowTyped();_show.Unchecked+=(_,_)=>ShowTyped();_password.PasswordChanged+=(_,_)=>ShowTyped();
        _removePassword.Checked+=(_,_)=>{_password.Clear();_password.IsEnabled=_generate.IsEnabled=false;};_removePassword.Unchecked+=(_,_)=>_password.IsEnabled=_generate.IsEnabled=true;
        _generate.Click+=(_,_)=>{var generated=PasswordGenerator.Generate(20);try{_password.Password=new string(generated);_show.IsChecked=true;}finally{Array.Clear(generated);}};
        save.Click+=Save_Click;_bulk.Click+=(_,_)=>{BulkRequested=true;DialogResult=false;};Closing+=(_,_)=>{_password.Clear();_visible.Clear();};Loaded+=(_,_)=>_title.Focus();
        Layout();
    }
    private VaultItemKind Kind=>(VaultItemKind)((ComboBoxItem)_kind.SelectedItem).Tag;
    private void Layout()
    {
        var kind=Kind;
        _urlRow.Visibility=kind==VaultItemKind.Note?Visibility.Collapsed:Visibility.Visible;
        _userRow.Visibility=kind is VaultItemKind.Login or VaultItemKind.Account?Visibility.Visible:Visibility.Collapsed;
        _linkedRow.Visibility=kind==VaultItemKind.Account?Visibility.Visible:Visibility.Collapsed;
        _passwordRow.Visibility=kind is VaultItemKind.Login or VaultItemKind.Account?Visibility.Visible:Visibility.Collapsed;
        _bulk.Visibility=_private&&kind==VaultItemKind.Link?Visibility.Visible:Visibility.Collapsed;
    }
    private void ShowTyped(){var on=_show.IsChecked==true;_visible.Visibility=on?Visibility.Visible:Visibility.Collapsed;_visible.Text=on?_password.Password:"";}
    private void Save_Click(object sender,RoutedEventArgs e)
    {
        var kind=Kind;var hasPasswordField=kind is VaultItemKind.Login or VaultItemKind.Account;
        var url=kind==VaultItemKind.Note?"":_url.Text.Trim();
        if(url.Length>0&&!url.Contains("://",StringComparison.Ordinal))url="https://"+url;
        var draft=new VaultItemDraft(_existing?.Id,kind,_title.Text,url,hasPasswordField?_username.Text:"",kind==VaultItemKind.Account?_linked.Text:"",_notes.Text,_private);
        var typed=UiSecrets.Read(_password);
        try
        {
            VaultItemStore.Validate(draft.Kind,draft.Title,draft.Url,draft.Username,draft.LinkedAccount,draft.Notes);
            VaultItemStore.ValidateSecret(typed);
            // A kind without passwords drops any stored one; an empty field keeps the existing password.
            var remove=_removePassword.IsChecked==true||(!hasPasswordField&&_existing?.HasPassword==true);
            ReplacePassword=remove||(hasPasswordField&&typed.Length>0);
            Array.Clear(_taken);_taken=remove||!hasPasswordField?[]:typed;if(_taken!=typed)Array.Clear(typed);
            Draft=draft;DialogResult=true;
        }
        catch(ArgumentException error){Array.Clear(typed);_error.Text=error.Message;}
    }
}

/// <summary>Shows one password for 30 seconds. Excluded from screen capture where Windows supports it.</summary>
internal sealed class RevealWindow : Window
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd,uint affinity);
    private readonly TextBox _text=new(){IsReadOnly=true,FontFamily=new System.Windows.Media.FontFamily("Consolas"),FontSize=18};
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(1)};
    public RevealWindow(VaultItem item,char[] password)
    {
        Title="Lösenord · "+item.Title;Width=520;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new Thickness(26)};
        panel.Children.Add(new TextBlock{Text=item.Title,FontSize=23,FontWeight=FontWeights.SemiBold});
        panel.Children.Add(new TextBlock{Text=(item.Host.Length>0?item.Host+" · ":"")+(item.Username.Length>0?item.Username:"inget användarnamn"),Margin=new Thickness(0,6,0,12)});
        _text.Text=new string(password);panel.Children.Add(_text);
        var remaining=30;var info=new TextBlock{FontSize=12,TextWrapping=TextWrapping.Wrap};panel.Children.Add(info);
        void Tick(){info.Text=$"Stängs automatiskt om {remaining} s. Kopierat lösenord rensas från urklipp efter 30 s och hamnar inte i Windows urklippshistorik.";if(remaining--<=0)Close();}
        Tick();_timer.Tick+=(_,_)=>Tick();_timer.Start();
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};
        var copy=new Button{Content="Kopiera lösenord",Style=(Style)Application.Current.FindResource("Primary")};
        copy.Click+=(_,_)=>{SecureClipboard.Copy(_text.Text,true);Close();};
        buttons.Children.Add(new Button{Content="Stäng",IsCancel=true});buttons.Children.Add(copy);panel.Children.Add(buttons);Content=panel;
        SourceInitialized+=(_,_)=>SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle,0x11);
        Closing+=(_,_)=>{_timer.Stop();_text.Clear();};
    }
}

internal static class SecureClipboard
{
    private static DispatcherTimer? _timer;private static string? _secret;
    public static void Copy(string text,bool secret)
    {
        var data=new DataObject();data.SetText(text);
        if(secret)
        {
            // Windows: keep the secret out of clipboard history, cloud clipboard and clipboard monitors.
            data.SetData("ExcludeClipboardContentFromMonitorProcessing",new MemoryStream(new byte[4]));
            data.SetData("CanIncludeInClipboardHistory",new MemoryStream(new byte[4]));
            data.SetData("CanUploadToCloudClipboard",new MemoryStream(new byte[4]));
        }
        for(var attempt=0;;attempt++)
        {
            try{Clipboard.SetDataObject(data,false);break;}
            catch(COMException)when(attempt<5){Thread.Sleep(50);}
        }
        _timer?.Stop();_secret=secret?text:null;
        if(!secret)return;
        _timer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(30)};_timer.Tick+=(_,_)=>ClearIfOurs();_timer.Start();
    }
    public static void ClearIfOurs()
    {
        _timer?.Stop();_timer=null;if(_secret is null)return;
        try{if(Clipboard.ContainsText()&&Clipboard.GetText()==_secret)Clipboard.Clear();}catch(COMException){}
        _secret=null;
    }
}

internal sealed class FirefoxSetupWindow : Window
{
    private readonly TextBlock _state=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14),FontWeight=FontWeights.SemiBold};
    public FirefoxSetupWindow()
    {
        Title="Koppla Firefox till NordVault";Width=640;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new Thickness(26)};
        panel.Children.Add(new TextBlock{Text="Firefox-tillägget",FontSize=23,FontWeight=FontWeights.SemiBold});
        panel.Children.Add(new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12),Text=
            "När du loggar in på en webbplats frågar tillägget om lösenordet ska sparas i NordVault. På inloggningssidor kan du fylla i ett sparat lösenord; NordVault visar då en ruta där du bekräftar varje ifyllning.\n\n"+
            "1. Klicka Koppla nedan (registrerar NordVault för Firefox, bara för ditt Windows-konto).\n"+
            "2. Öppna about:debugging#/runtime/this-firefox i Firefox och välj ”Läs in tillfälligt tillägg…”.\n"+
            "3. Välj filen manifest.json i mappen som öppnas med Visa tilläggsmappen.\n\n"+
            "Ett tillfälligt tillägg försvinner när Firefox startas om. För permanent installation kan tillägget signeras gratis som olistat tillägg på addons.mozilla.org. "+
            "Stäng gärna av Firefox egen fråga: Inställningar → Integritet och säkerhet → Fråga om att spara inloggningar."});
        panel.Children.Add(_state);
        var buttons=new WrapPanel();
        var connect=new Button{Content="Koppla",Style=(Style)Application.Current.FindResource("Primary")};var folder=new Button{Content="Visa tilläggsmappen"};
        var disconnect=new Button{Content="Koppla bort"};var close=new Button{Content="Stäng",IsCancel=true};
        foreach(var b in new[]{connect,folder,disconnect,close})buttons.Children.Add(b);panel.Children.Add(buttons);Content=panel;
        void Show(){var registered=FirefoxRegistration.IsRegistered;_state.Text=registered?"✓ NordVault är kopplat till Firefox för ditt konto.":"Inte kopplat ännu.";disconnect.IsEnabled=registered;folder.IsEnabled=Directory.Exists(FirefoxRegistration.ExtensionFolder);}
        connect.Click+=(_,_)=>{try{FirefoxRegistration.Register();}catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Security.SecurityException){_state.Text="Kopplingen misslyckades: "+error.Message;return;}Show();};
        disconnect.Click+=(_,_)=>{try{FirefoxRegistration.Unregister();}catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Security.SecurityException){_state.Text=error.Message;return;}Show();};
        folder.Click+=(_,_)=>Process.Start(new ProcessStartInfo("explorer.exe"){ArgumentList={FirefoxRegistration.ExtensionFolder},UseShellExecute=false});
        Show();
    }
}
