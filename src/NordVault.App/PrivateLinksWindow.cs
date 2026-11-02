using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NordVault.Core;

namespace NordVault.App;

/// <summary>
/// The Privat section: links with notes that never show in the main list or in Firefox. Opened only after the master
/// password or Windows Hello; links open in a private browser window. Hidden from screenshots; closes when the vault locks.
/// </summary>
internal sealed class PrivateLinksWindow : Window
{
    private readonly VaultSession _session;
    private readonly DataGrid _items=new(){AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,SelectionMode=DataGridSelectionMode.Extended,HeadersVisibility=DataGridHeadersVisibility.Column,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,RowHeight=32,MinHeight=200};
    private readonly TextBox _search=new(){Margin=new Thickness(0,0,0,10)};
    private readonly Button _add=new(){Content="+ Länk"},_bulk=new(){Content="+ Många länkar"},_fetch=new(){Content="Hämta från vanliga listan…"};
    private readonly Button _open=new(){Content="Öppna i privat fönster"},_edit=new(){Content="Redigera…"},_move=new(){Content="Flytta till vanliga listan"},_delete=new(){Content="Ta bort…"};
    private readonly StackPanel _browsers=new(){Orientation=Orientation.Horizontal,Margin=new Thickness(0,8,0,8)};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,0)};
    private bool _busy;

    public PrivateLinksWindow(VaultSession session)
    {
        _session=session;Title="NordVault · Privat";Width=1000;Height=720;MinWidth=760;MinHeight=520;WindowStartupLocation=WindowStartupLocation.CenterOwner;CaptureGuard.Apply(this);
        _open.Style=(Style)Application.Current.FindResource("Primary");
        var grid=new Grid{Margin=new Thickness(24)};grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});grid.RowDefinitions.Add(new RowDefinition());grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var top=new StackPanel();
        top.Children.Add(new TextBlock{Text="Privat",FontSize=25,FontWeight=FontWeights.SemiBold});
        top.Children.Add(new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,12),Text="Privata länkar med anteckningar. De syns inte i vanliga listan eller i Firefox, öppnas i ett privat fönster och stängs när valvet låses. Dubbelklicka för att öppna."});
        var add=new WrapPanel();add.Children.Add(_add);add.Children.Add(_bulk);add.Children.Add(_fetch);top.Children.Add(add);
        var search=new DockPanel();var label=new TextBlock{Text="Sök:",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,10,10)};DockPanel.SetDock(label,Dock.Left);search.Children.Add(label);search.Children.Add(_search);top.Children.Add(search);
        grid.Children.Add(top);
        _items.Columns.Add(new DataGridTextColumn{Header="Titel",Binding=new System.Windows.Data.Binding("Title"),Width=new DataGridLength(1,DataGridLengthUnitType.Star),MinWidth=200});
        _items.Columns.Add(new DataGridTextColumn{Header="Webbplats",Binding=new System.Windows.Data.Binding("Host"),Width=200});
        _items.Columns.Add(new DataGridTextColumn{Header="Anteckning",Binding=new System.Windows.Data.Binding("Notes"),Width=220});
        _items.Columns.Add(new DataGridTextColumn{Header="Ändrad",Binding=new System.Windows.Data.Binding("ModifiedText"),Width=130});
        Grid.SetRow(_items,1);grid.Children.Add(_items);
        var bottom=new StackPanel();
        var browserRow=new WrapPanel{Margin=new Thickness(0,10,0,0)};browserRow.Children.Add(new TextBlock{Text="Öppna i:",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,12,0)});browserRow.Children.Add(_browsers);bottom.Children.Add(browserRow);
        var actions=new WrapPanel();foreach(var b in new[]{_open,_edit,_move,_delete})actions.Children.Add(b);bottom.Children.Add(actions);bottom.Children.Add(_status);
        Grid.SetRow(bottom,2);grid.Children.Add(bottom);Content=grid;

        var installed=PrivateBrowser.Installed;var preferred=PrivateBrowser.Preferred;
        foreach(var browser in installed)
        {
            var radio=new RadioButton{Content=browser.Name,GroupName="browser",Margin=new Thickness(0,0,18,0),IsChecked=browser==preferred,Tag=browser};
            radio.Checked+=(_,_)=>PrivateBrowser.Preferred=browser;_browsers.Children.Add(radio);
        }
        if(installed.Length==0)_browsers.Children.Add(new TextBlock{Text="Ingen Firefox, Chrome eller Edge hittades.",Foreground=System.Windows.Media.Brushes.Firebrick});

        _search.TextChanged+=(_,_)=>Refresh();_items.SelectionChanged+=(_,_)=>Update();
        _items.MouseDoubleClick+=(_,e)=>{if(e.ChangedButton==MouseButton.Left)OpenSelected();};
        _items.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){OpenSelected();e.Handled=true;}};
        _add.Click+=(_,_)=>Edit(null);_edit.Click+=(_,_)=>{if(Selected.Length==1)Edit(Selected[0]);};
        _bulk.Click+=(_,_)=>AddMany();_fetch.Click+=(_,_)=>Fetch();_open.Click+=(_,_)=>OpenSelected();
        _move.Click+=async(_,_)=>{var chosen=Selected;if(chosen.Length==0)return;await RunAsync(async()=>{await _session.SetPrivateAsync(chosen.Select(i=>i.Id).ToArray(),false);Say($"{chosen.Length} länkar flyttade till vanliga listan.");});};
        _delete.Click+=async(_,_)=>
        {
            var chosen=Selected;if(chosen.Length==0)return;
            if(MessageBox.Show(this,chosen.Length==1?$"Ta bort ”{chosen[0].Title}”?":$"Ta bort {chosen.Length} privata länkar?","Ta bort",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
            await RunAsync(async()=>{foreach(var item in chosen)await _session.DeleteItemAsync(item.Id);Say($"{chosen.Length} länkar borttagna.");});
        };
        Refresh();Say(installed.Length==0?"Installera Firefox eller Chrome för att öppna länkar privat.":"Välj webbläsare ovan; valet sparas.");
    }

    private VaultItem[] Selected=>_items.SelectedItems.OfType<VaultItem>().ToArray();
    private void Say(string text)=>_status.Text=text;
    private void Refresh()
    {
        var ids=Selected.Select(i=>i.Id).ToHashSet();
        var list=_session.Items.Where(i=>i.Private&&i.Matches(_search.Text)).OrderBy(i=>i.Title,StringComparer.CurrentCultureIgnoreCase).ToArray();
        _items.ItemsSource=list;foreach(var item in list.Where(i=>ids.Contains(i.Id)))_items.SelectedItems.Add(item);
        Title=$"NordVault · Privat ({_session.Items.Count(i=>i.Private)})";Update();
    }
    private void Update()
    {
        var n=Selected.Length;var ready=!_busy&&!_session.IsBusy;
        _add.IsEnabled=_bulk.IsEnabled=_fetch.IsEnabled=ready;
        _open.IsEnabled=n>=1&&n<=10&&PrivateBrowser.Installed.Length>0;_edit.IsEnabled=ready&&n==1;_move.IsEnabled=_delete.IsEnabled=ready&&n>0;
    }
    private async Task RunAsync(Func<Task> work)
    {
        if(_busy)return;_busy=true;Update();
        try{await work();}
        catch(Exception error)when(error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException){Say(error.Message);}
        finally{_busy=false;Refresh();}
    }

    private void OpenSelected()
    {
        var chosen=Selected;if(chosen.Length==0)return;
        if(chosen.Length>10){Say("Välj högst 10 länkar åt gången.");return;}
        var browser=_browsers.Children.OfType<RadioButton>().FirstOrDefault(r=>r.IsChecked==true)?.Tag as PrivateBrowser.Browser??PrivateBrowser.Preferred;
        if(browser is null){Say("Ingen webbläsare hittades.");return;}
        try{foreach(var item in chosen)PrivateBrowser.Open(browser,item.Url);Say(chosen.Length==1?$"”{chosen[0].Title}” öppnad i {browser.Name}.":$"{chosen.Length} länkar öppnade i {browser.Name}.");}
        catch(Exception error)when(error is IOException or ArgumentException or System.ComponentModel.Win32Exception){Say("Kunde inte öppna: "+error.Message);}
    }
    private async void Edit(VaultItem? item)
    {
        var editor=new ItemEditorWindow(item,VaultItemKind.Link,[],true){Owner=this};
        if(editor.ShowDialog()!=true||editor.Draft is null){if(editor.BulkRequested)AddMany();return;}
        var password=editor.TakePassword();
        try{await RunAsync(async()=>{var saved=await _session.SaveItemAsync(editor.Draft,password,false);Say($"”{saved.Title}” sparad i Privat.");});}
        finally{Array.Clear(password);}
    }
    private async void AddMany()
    {
        var dialog=new BulkLinksWindow(true){Owner=this};
        if(dialog.ShowDialog()!=true||dialog.Links.Count==0)return;
        await RunAsync(async()=>{var summary=await _session.ImportItemsAsync([],dialog.Links);Say($"{summary.Created} nya privata länkar sparade"+(summary.Skipped>0?$", {summary.Skipped} fanns redan i Privat.":"."));});
    }
    private async void Fetch()
    {
        var picker=new PickLinksWindow(_session.Items.Where(i=>i.Kind==VaultItemKind.Link&&!i.Private)){Owner=this};
        if(picker.ShowDialog()!=true||picker.Chosen.Count==0)return;
        await RunAsync(async()=>{await _session.SetPrivateAsync(picker.Chosen,true);Say($"{picker.Chosen.Count} länkar flyttade till Privat. De syns inte längre i vanliga listan.");});
    }
}

/// <summary>Choose links from the main list to move into Privat.</summary>
internal sealed class PickLinksWindow : Window
{
    private readonly ListBox _list=new(){SelectionMode=SelectionMode.Extended,MinHeight=320};
    private readonly TextBox _search=new();
    private readonly VaultItem[] _all;
    private readonly HashSet<string> _picked=[];
    public List<string> Chosen{get;private set;}=[];
    public PickLinksWindow(IEnumerable<VaultItem> links)
    {
        _all=links.OrderBy(i=>i.Title,StringComparer.CurrentCultureIgnoreCase).ToArray();
        Title="Hämta länkar till Privat";Width=760;Height=680;WindowStartupLocation=WindowStartupLocation.CenterOwner;CaptureGuard.Apply(this);
        var grid=new Grid{Margin=new Thickness(24)};grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});grid.RowDefinitions.Add(new RowDefinition());grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var top=new StackPanel();
        top.Children.Add(new TextBlock{Text="Välj länkar att flytta till Privat",FontSize=21,FontWeight=FontWeights.SemiBold});
        top.Children.Add(new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,10),Text="Sök och markera (Ctrl-klick för flera, Skift-klick för ett intervall). Valda länkar flyttas från vanliga listan till Privat."});
        top.Children.Add(_search);grid.Children.Add(top);
        Grid.SetRow(_list,1);grid.Children.Add(_list);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        var all=new Button{Content="Markera alla som visas"};var ok=new Button{Content="Flytta valda till Privat",Style=(Style)Application.Current.FindResource("Primary")};
        buttons.Children.Add(all);buttons.Children.Add(new Button{Content="Avbryt",IsCancel=true});buttons.Children.Add(ok);Grid.SetRow(buttons,2);grid.Children.Add(buttons);Content=grid;
        _search.TextChanged+=(_,_)=>Fill();
        _list.SelectionChanged+=(_,e)=>{foreach(ListBoxItem i in e.AddedItems)_picked.Add((string)i.Tag);foreach(ListBoxItem i in e.RemovedItems)_picked.Remove((string)i.Tag);ok.Content=$"Flytta valda till Privat ({_picked.Count})";};
        all.Click+=(_,_)=>_list.SelectAll();
        ok.Click+=(_,_)=>{Chosen=[.._picked];DialogResult=true;};
        Fill();Loaded+=(_,_)=>_search.Focus();
    }
    private void Fill()
    {
        var keep=_picked.ToHashSet();_list.Items.Clear();
        foreach(var item in _all.Where(i=>i.Matches(_search.Text)))
        {
            var row=new ListBoxItem{Content=item.Title+"   ·   "+item.Host,Tag=item.Id};_list.Items.Add(row);
            if(keep.Contains(item.Id))row.IsSelected=true;
        }
        _picked.Clear();foreach(var id in keep)_picked.Add(id);
    }
}
