using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NordVault.Core;

namespace NordVault.App;
/// <summary>Logins, links, notes and linked accounts. Stored passwords are only shown or copied after the master password.</summary>
public sealed class PasswordsPanel : UserControl
{
    private readonly VaultSession? _session;private readonly bool _demo;
    private readonly DataGrid _items=new(){AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,SelectionMode=DataGridSelectionMode.Single,MinHeight=170,HeadersVisibility=DataGridHeadersVisibility.Column,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,RowHeight=36};
    private readonly TextBox _search=new(){Margin=new Thickness(0,0,0,12)};
    private readonly Border _changeBanner=new(){Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,242,219)),CornerRadius=new CornerRadius(9),Padding=new Thickness(14,8,14,8),Margin=new Thickness(0,0,0,10),Visibility=Visibility.Collapsed};
    private readonly TextBlock _changeText=new(){TextWrapping=TextWrapping.Wrap,Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(115,85,29)),Margin=new Thickness(0,0,0,8)};
    private readonly Button _changeToggle=new(){HorizontalAlignment=HorizontalAlignment.Left};
    private bool _onlyChange;
    private readonly Button _newLogin=new(){Content="+ Inloggning",Style=(Style)Application.Current.FindResource("Primary")};private readonly Button _private=new(){Content="🔒 Privat…"};private readonly Button _newLink=new(){Content="+ Länk"};private readonly Button _newLinks=new(){Content="+ Många länkar"};
    private readonly Button _newNote=new(){Content="+ Anteckning"};private readonly Button _newAccount=new(){Content="+ Kopplat konto"};
    private readonly Button _edit=new(){Content="Redigera…"};private readonly Button _delete=new(){Content="Ta bort…"};
    private readonly Button _openSite=new(){Content="Öppna webbplats"};private readonly Button _copyUser=new(){Content="Kopiera användarnamn"};
    private readonly Button _reveal=new(){Content="Visa / kopiera lösenord…"};private readonly Button _firefox=new(){Content="Koppla Firefox…"};private readonly Button _import=new(){Content="Importera från Firefox…"};private readonly Button _leakCheck=new(){Content="Läckkontroll…"};
    private readonly Button _open=new(){Content="Lås upp valvet…"};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,0)};
    private bool _blocked;
    public bool IsBusy{get;private set;}
    public event Action? BusyChanged;public event Action<string>? ActivityChanged;public event Action? OpenRequested;public event Action? LockRequired;
    public PasswordsPanel(VaultSession? session,bool demo)
    {
        _session=session;_demo=demo;
        if(session is null&&!demo)
        {
            var welcome=new StackPanel{Margin=new Thickness(28,32,28,22)};
            welcome.Children.Add(new TextBlock{Text="Lösenord, länkar och anteckningar",FontSize=27,FontWeight=FontWeights.SemiBold});
            welcome.Children.Add(new TextBlock{Text="Spara inloggningar till webbplatser, länkar, anteckningar och konton som du loggar in på via ett annat konto (till exempel Google). Allt krypteras med valvets huvudlösenord.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,20)});
            _open.HorizontalAlignment=HorizontalAlignment.Left;_open.Style=(Style)Application.Current.FindResource("Primary");welcome.Children.Add(_open);
            welcome.Children.Add(new TextBlock{Text="Ett sparat lösenord visas eller kopieras bara när du anger huvudlösenordet igen, även när valvet redan är upplåst.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,24,0,0)});
            _open.Click+=(_,_)=>{if(!_blocked)OpenRequested?.Invoke();};Content=welcome;return;
        }
        var panel=new Grid{Margin=new Thickness(20,16,20,10)};panel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});panel.RowDefinitions.Add(new RowDefinition{MinHeight=150});panel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var heading=new StackPanel();
        heading.Children.Add(new TextBlock{Text="Krypterat i valvet. Lösenord visas aldrig i listan; att visa eller kopiera kräver huvudlösenordet. Dubbelklicka för att redigera.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)});
        var add=new WrapPanel();foreach(var b in new[]{_newLogin,_newNote,_newAccount,_import,_private})add.Children.Add(b);heading.Children.Add(add);
        var banner=new DockPanel();_changeToggle.Margin=new Thickness(12,0,0,0);_changeToggle.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(_changeToggle,Dock.Right);
        _changeText.Margin=new Thickness(0);_changeText.VerticalAlignment=VerticalAlignment.Center;banner.Children.Add(_changeToggle);banner.Children.Add(_changeText);_changeBanner.Child=banner;heading.Children.Add(_changeBanner);
        var search=new DockPanel();var searchLabel=new TextBlock{Text="Sök:",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,6)};DockPanel.SetDock(searchLabel,Dock.Left);
        _search.Margin=new Thickness(0,0,0,10);search.Children.Add(searchLabel);search.Children.Add(_search);heading.Children.Add(search);panel.Children.Add(heading);
        _items.Columns.Add(new DataGridTextColumn{Header="Typ",Binding=new System.Windows.Data.Binding("KindText"),Width=110});
        _items.Columns.Add(new DataGridTextColumn{Header="Att byta",Binding=new System.Windows.Data.Binding("ChangeText"),Width=200});
        _items.Columns.Add(new DataGridTextColumn{Header="Titel",Binding=new System.Windows.Data.Binding("Title"),Width=new DataGridLength(1,DataGridLengthUnitType.Star),MinWidth=160});
        _items.Columns.Add(new DataGridTextColumn{Header="Webbplats",Binding=new System.Windows.Data.Binding("Host"),Width=190});
        _items.Columns.Add(new DataGridTextColumn{Header="Användarnamn",Binding=new System.Windows.Data.Binding("Username"),Width=180});
        _items.Columns.Add(new DataGridTextColumn{Header="Ändrad",Binding=new System.Windows.Data.Binding("ModifiedText"),Width=135});
        Grid.SetRow(_items,1);panel.Children.Add(_items);
        var bottom=new StackPanel();var buttons=new WrapPanel{Margin=new Thickness(0,14,0,0)};foreach(var b in new[]{_edit,_openSite,_copyUser,_reveal,_delete,_leakCheck,_firefox})buttons.Children.Add(b);
        bottom.Children.Add(buttons);bottom.Children.Add(_status);Grid.SetRow(bottom,2);panel.Children.Add(bottom);Content=panel;
        _search.TextChanged+=(_,_)=>Refresh();_items.SelectionChanged+=(_,_)=>Update();_items.MouseDoubleClick+=(_,e)=>{if(e.ChangedButton==MouseButton.Left)Edit();};
        _newLogin.Click+=(_,_)=>Create(VaultItemKind.Login);_newLink.Click+=(_,_)=>Create(VaultItemKind.Link);_newLinks.Click+=(_,_)=>AddManyLinks();_private.Click+=(_,_)=>OpenPrivate();_newNote.Click+=(_,_)=>Create(VaultItemKind.Note);_newAccount.Click+=(_,_)=>Create(VaultItemKind.Account);
        _edit.Click+=(_,_)=>Edit();_delete.Click+=Delete_Click;_openSite.Click+=(_,_)=>OpenSite();_copyUser.Click+=(_,_)=>{if(Selected is {} item&&item.Username.Length>0){SecureClipboard.Copy(item.Username,false);Status("Användarnamnet är kopierat.");}};
        _reveal.Click+=(_,_)=>Reveal();_firefox.Click+=(_,_)=>new FirefoxSetupWindow{Owner=Window.GetWindow(this)}.ShowDialog();_import.Click+=Import_Click;_leakCheck.Click+=LeakCheck_Click;_changeToggle.Click+=(_,_)=>{_onlyChange=!_onlyChange;Refresh();};
        Refresh();Status(demo?"DEMO · inga riktiga poster.":"Klart. Tips: koppla Firefox så frågar NordVault om att spara lösenord när du loggar in.");
    }
    private VaultItem? Selected=>_items.SelectedItem as VaultItem;
    public void SetBlocked(bool blocked){_blocked=blocked;_open.IsEnabled=!blocked;Update();}
    public void Clear(){_items.ItemsSource=null;_status.Text="Valvet är låst.";}
    public void Refresh()
    {
        var id=Selected?.Id;
        IEnumerable<VaultItem> source=(_demo?DemoItems():_session?.Items??[]).Where(i=>!i.Private);
        var all=source.ToArray();var toChange=all.Count(i=>i.NeedsAttention);var leaked=all.Count(i=>i.MustChange);
        if(toChange==0)_onlyChange=false;
        _changeBanner.Visibility=toChange>0?Visibility.Visible:Visibility.Collapsed;
        var breached=all.Count(i=>i.Issue.HasFlag(PasswordIssue.Breached));
        _changeText.Text=$"⚠ {toChange} lösenord att byta"+(breached>0?$" – {breached} finns i kända läckor (byt dem först, de står överst)":"")+(leaked>0?$" – {leaked} låg i Firefox och kan ha läckt":"")+
            ". Även återanvända och svaga lösenord visas. Byt på webbplatsen och spara det nya här, så försvinner markeringen.";
        _changeToggle.Content=_onlyChange?"Visa alla poster":$"Visa listan Att byta ({toChange})";
        var filtered=all.Where(i=>i.Matches(_search.Text)&&(!_onlyChange||i.NeedsAttention));
        var list=(_onlyChange?filtered.OrderByDescending(i=>i.Issue.HasFlag(PasswordIssue.Breached)).ThenBy(i=>i.ChangePriority.Rank).ThenBy(i=>i.Title,StringComparer.CurrentCultureIgnoreCase):filtered.OrderBy(i=>i.Title,StringComparer.CurrentCultureIgnoreCase)).ToArray();
        _items.ItemsSource=list;_items.SelectedItem=list.FirstOrDefault(i=>i.Id==id);Update();
    }
    private static VaultItem[] DemoItems(){var now=DateTimeOffset.UtcNow;return[new(Guid.NewGuid().ToString("N"),VaultItemKind.Login,"Banken","https://bank.example.se","anna@example.se","","",true,now,now,true),new(Guid.NewGuid().ToString("N"),VaultItemKind.Link,"Kommunens e-tjänster","https://kommun.example.se","","","",false,now,now),new(Guid.NewGuid().ToString("N"),VaultItemKind.Account,"Forumet","https://forum.example.se","anna","Google-kontot","",false,now,now),new(Guid.NewGuid().ToString("N"),VaultItemKind.Note,"Portkod","","","","Portkod 4711",false,now,now)];}
    private void Update()
    {
        var ready=!_demo&&_session is not null&&!_blocked&&!IsBusy;var item=Selected;
        _newLogin.IsEnabled=_newLink.IsEnabled=_newLinks.IsEnabled=_newNote.IsEnabled=_newAccount.IsEnabled=_import.IsEnabled=_private.IsEnabled=ready;_leakCheck.IsEnabled=ready&&_session!.Items.Any(i=>i.HasPassword);_firefox.IsEnabled=!_demo;
        _edit.IsEnabled=_delete.IsEnabled=ready&&item is not null;
        _openSite.IsEnabled=!_demo&&item is not null&&VaultItemMatcher.Parse(item.Url) is not null;
        _copyUser.IsEnabled=!_demo&&item is {Username.Length:>0};
        _reveal.IsEnabled=ready&&item is {HasPassword:true};
    }
    private void Status(string text){_status.Text=text;ActivityChanged?.Invoke(text);}
    private async Task RunAsync(Func<CancellationToken,Task> operation)
    {
        if(IsBusy||_blocked||_session is null||_demo)return;IsBusy=true;Update();BusyChanged?.Invoke();
        try{await operation(CancellationToken.None);}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException){Status(error is UnauthorizedAccessException?"Windows nekade åtkomst till valvmappen.":error.Message);}
        finally{IsBusy=false;Refresh();BusyChanged?.Invoke();}
    }
    private void Create(VaultItemKind kind)=>Save(new ItemEditorWindow(null,kind,LoginTitles()){Owner=Window.GetWindow(this)});
    private void Edit(){if(Selected is {} item&&_edit.IsEnabled)Save(new ItemEditorWindow(item,item.Kind,LoginTitles()){Owner=Window.GetWindow(this)});}
    private string[] LoginTitles()=>_session?.Items.Where(i=>i.Kind==VaultItemKind.Login).Select(i=>i.Title).Distinct().OrderBy(t=>t).ToArray()??[];
    /// <summary>Privat needs the master password or Windows Hello again, also when the vault is open.</summary>
    private async void OpenPrivate()
    {
        if(!_private.IsEnabled||_session is null)return;
        var session=_session;var owner=Window.GetWindow(this);
        var prompt=new SecretWindow("Öppna Privat","Ange valvets huvudlösenord för att öppna Privat. Efter tre fel låses valvet.","Öppna",session.HelloEnabled){Owner=owner};
        if(prompt.ShowDialog()!=true){prompt.Clear();return;}
        var master=prompt.UseHello?[]:prompt.TakeSecret();prompt.Clear();bool ok;
        IsBusy=true;Update();BusyChanged?.Invoke();
        try{Status(prompt.UseHello?"Bekräfta med Windows Hello…":"Kontrollerar huvudlösenordet…");ok=await session.UnlockPrivateAsync(master,new System.Windows.Interop.WindowInteropHelper(owner).Handle,prompt.UseHello);}
        finally{Array.Clear(master);IsBusy=false;Update();BusyChanged?.Invoke();}
        if(!ok)
        {
            if(session.MasterPasswordLockout){Status("Tre fel huvudlösenord. Valvet låses.");LockRequired?.Invoke();}
            else Status(prompt.UseHello?"Windows Hello godkände inte.":"Fel huvudlösenord. Privat öppnades inte.");
            return;
        }
        Status("Privat är öppet.");
        new PrivateLinksWindow(session){Owner=owner}.ShowDialog();
        Refresh();Status("Privat är stängt.");
    }
    private async void AddManyLinks()
    {
        if(!_newLinks.IsEnabled)return;
        var dialog=new BulkLinksWindow{Owner=Window.GetWindow(this)};
        if(dialog.ShowDialog()!=true||dialog.Links.Count==0)return;
        await RunAsync(async cancellation=>
        {
            Status($"Krypterar och sparar {dialog.Links.Count} länkar…");
            var summary=await _session!.ImportItemsAsync([],dialog.Links,cancellation);
            Status($"{summary.Created} nya länkar sparade krypterat"+(summary.Skipped>0?$", {summary.Skipped} fanns redan.":"."));
        });
    }
    private async void Save(ItemEditorWindow editor)
    {
        if(editor.ShowDialog()!=true||editor.Draft is null){if(editor.BulkRequested)AddManyLinks();return;}var password=editor.TakePassword();
        try{await RunAsync(async cancellation=>{Status("Krypterar och sparar i valvet…");var item=await _session!.SaveItemAsync(editor.Draft,password,editor.ReplacePassword,cancellation);Status($"”{item.Title}” är sparad krypterat.");});}
        finally{Array.Clear(password);}
    }
    private async void Delete_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is not {} item||!_delete.IsEnabled)return;
        if(MessageBox.Show(Window.GetWindow(this),$"Ta bort ”{item.Title}” ({item.KindText}) från valvet?\n\nFöregående version av lösenordsfilen sparas som items.previous.gpg tills nästa ändring.","Ta bort post",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        await RunAsync(async cancellation=>{await _session!.DeleteItemAsync(item.Id,cancellation);Status($"”{item.Title}” är borttagen.");});
    }
    private async void Import_Click(object sender,RoutedEventArgs e)
    {
        if(!_import.IsEnabled)return;
        var dialog=new FirefoxImportWindow{Owner=Window.GetWindow(this)};var importedLogins=false;
        if(dialog.ShowDialog()!=true){dialog.Discard();return;}
        try
        {
            await RunAsync(async cancellation=>
            {
                Status($"Krypterar {dialog.Logins.Count} inloggningar och {dialog.Bookmarks.Count} bokmärken…");
                var summary=await _session!.ImportItemsAsync(dialog.Logins,dialog.Bookmarks,cancellation);
                var deleted=dialog.DeleteLoginFiles();
                Status(dialog.Logins.Count==0
                    ?$"Import klar: {summary.Created} nya länkar, {summary.Skipped} fanns redan."+deleted
                    :$"Import klar: {summary.Created} nya, {summary.Updated} uppdaterade lösenord, {summary.Skipped} fanns redan eller hoppades över. {summary.Flagged} lösenord är markerade Att byta."+deleted);
                if(summary.Flagged>0){_onlyChange=true;importedLogins=true;}
            });
            if(importedLogins)new FirefoxCleanupWindow{Owner=Window.GetWindow(this)}.ShowDialog();
        }
        finally{dialog.Discard();}
    }
    private async void LeakCheck_Click(object sender,RoutedEventArgs e)
    {
        if(!_leakCheck.IsEnabled)return;
        if(MessageBox.Show(Window.GetWindow(this),
            "NordVault kontrollerar om dina sparade lösenord finns i kända dataläckor, via tjänsten Have I Been Pwned (api.pwnedpasswords.com).\n\n"+
            "Så skyddas du: för varje lösenord räknas en kontrollsumma (SHA-1) ut på datorn. Bara de 5 första tecknen av den skickas. Tjänsten svarar med hundratals möjliga träffar som jämförs här på datorn. Lösenordet, resten av kontrollsumman, användarnamn och webbplatser skickas aldrig.\n\n"+
            "Tjänsten ser din IP-adress. Kontrollen görs bara nu, när du klickar.\n\nStarta läckkontrollen?",
            "Läckkontroll",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        await RunAsync(async cancellation=>
        {
            try
            {
                var count=await _session!.CheckBreachesAsync(PwnedPasswords.FetchRangeAsync,new Progress<string>(Status),cancellation);
                if(count>0)_onlyChange=true;
                Status(count==0?"Inget av dina lösenord finns i kända läckor.":$"⚠ {count} lösenord finns i kända läckor och står nu först i listan Att byta. Byt dem så snart som möjligt.");
            }
            catch(Exception error)when(error is System.Net.Http.HttpRequestException or TaskCanceledException){Status("Läckkontrollen kunde inte nå tjänsten. Kontrollera internetanslutningen. Inget har ändrats.");}
        });
    }
    private void OpenSite()
    {
        if(Selected is not {} item||VaultItemMatcher.Parse(item.Url) is not {} uri)return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});
    }
    private async void Reveal()
    {
        if(Selected is not {} item||!_reveal.IsEnabled||_session is null)return;
        var session=_session;var owner=Window.GetWindow(this);
        var prompt=new SecretWindow("Bekräfta att det är du",$"Ange valvets huvudlösenord för att visa lösenordet till ”{item.Title}”. Efter tre fel låses valvet.","Visa",session.HelloEnabled){Owner=owner};
        if(prompt.ShowDialog()!=true){prompt.Clear();return;}
        var master=prompt.UseHello?[]:prompt.TakeSecret();prompt.Clear();char[] secret;
        IsBusy=true;Update();BusyChanged?.Invoke();
        try
        {
            Status(prompt.UseHello?"Bekräfta med Windows Hello…":"Kontrollerar huvudlösenordet…");
            secret=prompt.UseHello?await session.RevealPasswordWithHelloAsync(item.Id,new System.Windows.Interop.WindowInteropHelper(owner).Handle):await session.RevealPasswordAsync(item.Id,master);
        }
        catch(UnauthorizedAccessException)
        {
            if(session.MasterPasswordLockout){Status("Tre fel huvudlösenord. Valvet låses.");IsBusy=false;BusyChanged?.Invoke();LockRequired?.Invoke();}
            else Status(prompt.UseHello?"Windows Hello godkände inte. Lösenordet visades inte.":"Fel huvudlösenord. Lösenordet visades inte.");
            return;
        }
        finally{Array.Clear(master);IsBusy=false;Update();BusyChanged?.Invoke();}
        try{new RevealWindow(item,secret){Owner=owner}.ShowDialog();Status("Lösenordsrutan är stängd.");}
        finally{Array.Clear(secret);}
    }
}
