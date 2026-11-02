using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NordVault.Core;

namespace NordVault.App;
public sealed class VaultPanel : UserControl
{
    private readonly VaultSession? _session;private readonly bool _demo;
    private readonly DataGrid _files=new(){AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,SelectionMode=DataGridSelectionMode.Single,MinHeight=170};
    private readonly Button _import=new(){Content="Lägg till filer eller nyckelfiler…"};private readonly Button _export=new(){Content="Spara dekrypterad kopia…"};
    private readonly Button _remove=new(){Content="Ta bort ur valvet…"};
    private readonly Button _changeMaster=new(){Content="Byt huvudlösenord…"};private readonly Button _codes=new(){Content="Återställningskoder…"};private readonly Button _hello=new(){Content="Windows Hello…"};
    private readonly TextBlock _security=new(){TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,0,0,6)};
    private readonly Button _cancel=new(){Content="Avbryt",IsEnabled=false};private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)};
    private bool _blocked;private CancellationTokenSource? _cancellation;
    private readonly Button _open=new(){Content="Skapa eller öppna valv…"};
    public event Action? OpenRequested;
    public bool IsBusy{get;private set;}public event Action? BusyChanged;public event Action<string>? ActivityChanged;
    internal bool CanOpenVault=>_open.IsEnabled;
    public VaultPanel(VaultSession? session,bool demo,bool previewClosed=false)
    {
        _session=session;_demo=demo;
        if(session is null&&(!demo||previewClosed))
        {
            var welcome=new StackPanel{Margin=new Thickness(28,32,28,22)};
            welcome.Children.Add(new TextBlock{Text="Ditt valv är valfritt",FontSize=27,FontWeight=FontWeights.SemiBold});
            welcome.Children.Add(new TextBlock{Text="Använd diskar, USB och filkryptering direkt. Om du vill samla filer och nyckelfiler under ett huvudlösenord kan du skapa eller öppna ett valv här.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,20)});
            welcome.Children.Add(new TextBlock{Text="Ingen fast diskyta reserveras. Valvet är en mapp med krypterade filer och växer bara när du lägger till innehåll. Befintliga valv från version 0.3.0 fungerar här.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,24)});
            _open.HorizontalAlignment=HorizontalAlignment.Left;_open.Style=(Style)Application.Current.FindResource("Primary");welcome.Children.Add(_open);
            welcome.Children.Add(new TextBlock{Text="Ett stängt valv visar inga filnamn. Originalen behålls när du importerar och klartextexport kräver valvets lösenord.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,24,0,0)});
            _open.IsEnabled=!demo;_open.Click+=(_,_)=>{if(!_blocked&&!_demo)OpenRequested?.Invoke();};Content=welcome;return;
        }
        var panel=new Grid{Margin=new Thickness(20,22,20,15)};panel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});panel.RowDefinitions.Add(new RowDefinition());panel.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var heading=new StackPanel();heading.Children.Add(new TextBlock{Text="Mitt krypterade valv",FontSize=25,FontWeight=FontWeights.SemiBold});
        heading.Children.Add(new TextBlock{Text="Filer och nyckelfiler skyddas av huvudlösenordet. Originalet behålls vid import. Lås appen innan du lämnar datorn.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,12)});
        heading.Children.Add(new TextBlock{Text=demo?@"F:\NordVault\Data\Vault":session?.Root,TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,0,0,15)});
        var autoLock=new ComboBox{Width=110,Margin=new Thickness(10,0,0,0),IsEnabled=!demo};foreach(var m in AppPaths.AutoLockChoices)autoLock.Items.Add(new ComboBoxItem{Content=m+" minuter",Tag=m});
        autoLock.SelectedItem=autoLock.Items.Cast<ComboBoxItem>().First(i=>(int)i.Tag==(demo?10:AppPaths.AutoLockMinutes));
        autoLock.SelectionChanged+=(_,_)=>{try{AppPaths.SetAutoLockMinutes((int)((ComboBoxItem)autoLock.SelectedItem).Tag);Status("Autolås ändrat. Valvet låses också när Windows låses, vid viloläge och när skärmsläckaren startar.");}catch(Exception error)when(error is IOException or UnauthorizedAccessException){Status("Inställningen kunde inte sparas.");}};
        var lockRow=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,12)};lockRow.Children.Add(new TextBlock{Text="Lås automatiskt efter",VerticalAlignment=VerticalAlignment.Center});lockRow.Children.Add(autoLock);
        lockRow.Children.Add(new TextBlock{Text="utan aktivitet · alltid när Windows låses eller går i vila",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(10,0,0,0),FontSize=12});
        heading.Children.Add(lockRow);panel.Children.Add(heading);
        _files.Columns.Add(new DataGridTextColumn{Header="Fil / nyckelfil",Binding=new System.Windows.Data.Binding("Name"),MinWidth=320,Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
        _files.Columns.Add(new DataGridTextColumn{Header="Storlek",Binding=new System.Windows.Data.Binding("SizeText"),Width=100});
        _files.Columns.Add(new DataGridTextColumn{Header="Tillagd",Binding=new System.Windows.Data.Binding("AddedText"),Width=160});Grid.SetRow(_files,1);panel.Children.Add(_files);
        var bottom=new StackPanel();var buttons=new WrapPanel{Margin=new Thickness(0,16,0,0)};buttons.Children.Add(_import);buttons.Children.Add(_export);buttons.Children.Add(_remove);buttons.Children.Add(_cancel);bottom.Children.Add(buttons);
        var security=new WrapPanel();security.Children.Add(_changeMaster);security.Children.Add(_codes);security.Children.Add(_hello);
        bottom.Children.Add(new TextBlock{Text="Säkerhet",FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,12,0,4)});bottom.Children.Add(_security);bottom.Children.Add(security);bottom.Children.Add(_status);
        bottom.Children.Add(new TextBlock{Text="Export skapar en klartextkopia utanför valvet. För portabel användning: kopiera hela NordVault-mappen med Data-mappen till USB, eller öppna valvmappen från annan NordVault.",TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new Thickness(0,15,0,0)});Grid.SetRow(bottom,2);panel.Children.Add(bottom);Content=panel;
        _files.SelectionChanged+=(_,_)=>Update();_import.Click+=Import_Click;_export.Click+=Export_Click;_remove.Click+=Remove_Click;_changeMaster.Click+=ChangeMaster_Click;_codes.Click+=Codes_Click;_hello.Click+=Hello_Click;_cancel.Click+=(_,_)=>_cancellation?.Cancel();Refresh();
    }
    public void SetBlocked(bool blocked){_blocked=blocked;_open.IsEnabled=!blocked&&!_demo;Update();}
    public void Clear(){_files.ItemsSource=null;_status.Text="Valvet är låst.";}
    private void Refresh(){_files.ItemsSource=_demo?new[]{new VaultEntry(Guid.NewGuid().ToString("N"),"privat-nyckel.asc",4096,new string('A',64),DateTimeOffset.UtcNow),new VaultEntry(Guid.NewGuid().ToString("N"),"viktiga-handlingar.pdf",1250000,new string('B',64),DateTimeOffset.UtcNow)}:_session?.Entries.ToArray();Update();}
    private void Update(){var ready=!_demo&&_session is not null&&!_blocked&&!IsBusy;_import.IsEnabled=ready;_export.IsEnabled=_remove.IsEnabled=ready&&_files.SelectedItem is VaultEntry;
        _changeMaster.IsEnabled=_codes.IsEnabled=_hello.IsEnabled=ready;
        if(_session is not null&&!_demo)
        {
            var codes=_session.RecoveryCodeCount;var hello=_session.HelloEnabled;
            _security.Text=(codes>0?$"{codes} oanvända återställningskoder. ":"Inga återställningskoder – skapa dem, annars går valvet inte att öppna om du glömmer huvudlösenordet. ")+
                (hello?"Windows Hello är på för den här datorn (huvudlösenordet krävs efter omstart).":"Windows Hello är av.");
        }_cancel.IsEnabled=IsBusy;}
    private void Status(string text){_status.Text=text;ActivityChanged?.Invoke(text);}
    private async Task RunAsync(Func<CancellationToken,Task> operation)
    {
        if(IsBusy||_blocked||_session is null||_demo)return;IsBusy=true;_cancellation=new CancellationTokenSource();Update();BusyChanged?.Invoke();
        try{await operation(_cancellation.Token);}
        catch(OperationCanceledException){Status("Valvåtgärden avbröts. Redan färdigimporterade filer finns kvar i valvet.");}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException){Status(error is UnauthorizedAccessException?"Windows nekade åtkomst till valvet eller vald fil.":error.Message);}
        finally{IsBusy=false;_cancellation.Dispose();_cancellation=null;Refresh();BusyChanged?.Invoke();}
    }
    private async void Import_Click(object sender,RoutedEventArgs e)
    {
        if(!_import.IsEnabled)return;var dialog=new OpenFileDialog{Title="Lägg till filer eller nyckelfiler i krypterat valv",Multiselect=true,Filter="Alla filer|*.*"};if(dialog.ShowDialog(Window.GetWindow(this))!=true)return;
        await RunAsync(async cancellation=>{var count=0;foreach(var path in dialog.FileNames){Status($"Krypterar valvfil {count+1} av {dialog.FileNames.Length}…");await _session!.ImportAsync(path,cancellation);count++;}Status($"{count} filer lagrade krypterat. Originalfilerna finns kvar.");});
    }
    public event Action? LockRequired;
    private IntPtr Handle=>new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(this)).Handle;
    private char[]? AskMaster(string why)
    {
        var prompt=new SecretWindow("Bekräfta huvudlösenord",why,"Fortsätt"){Owner=Window.GetWindow(this)};
        if(prompt.ShowDialog()!=true){prompt.Clear();return null;}
        var master=prompt.TakeSecret();prompt.Clear();return master;
    }
    private void WrongMaster(){if(_session?.MasterPasswordLockout==true){Status("Tre fel huvudlösenord. Valvet låses.");LockRequired?.Invoke();}else Status("Fel huvudlösenord. Inget ändrades.");}
    private async void ChangeMaster_Click(object sender,RoutedEventArgs e)
    {
        if(!_changeMaster.IsEnabled)return;
        var dialog=new NewMasterPasswordWindow(true,"Det nya huvudlösenordet gäller direkt. Återställningskoderna fortsätter att fungera. Windows Hello stängs av och kan slås på igen.\n\nObs: valvets filer krypteras inte om. Den som redan har en kopia av valvmappen och känner till det gamla lösenordet kan fortfarande öppna den kopian.")
            {Owner=Window.GetWindow(this)};
        if(dialog.ShowDialog()!=true)return;var (current,next)=dialog.Take();var wrong=false;
        try{await RunAsync(async cancellation=>{try{await _session!.ChangeMasterPasswordAsync(current,next,cancellation);Status("Nytt huvudlösenord är sparat.");}catch(UnauthorizedAccessException){wrong=true;}});}
        finally{Array.Clear(current);Array.Clear(next);}
        if(wrong)WrongMaster();
    }
    private async void Codes_Click(object sender,RoutedEventArgs e)
    {
        if(!_codes.IsEnabled||_session is null)return;
        if(_session.RecoveryCodeCount>0&&MessageBox.Show(Window.GetWindow(this),"Nya koder ersätter alla tidigare koder, som slutar fungera. Fortsätta?","Återställningskoder",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        var master=AskMaster("Ange huvudlösenordet för att skapa nya återställningskoder.");if(master is null)return;
        List<char[]>? codes=null;var wrong=false;
        try{await RunAsync(async cancellation=>{try{Status("Skapar återställningskoder…");codes=await _session.CreateRecoveryCodesAsync(master,5,cancellation);}catch(UnauthorizedAccessException){wrong=true;}});}
        finally{Array.Clear(master);}
        if(wrong){WrongMaster();return;}
        if(codes is null)return;
        try{new RecoveryCodesWindow(codes){Owner=Window.GetWindow(this)}.ShowDialog();Status("Nya återställningskoder är skapade. De gamla fungerar inte längre.");}
        finally{foreach(var code in codes)Array.Clear(code);Update();}
    }
    private async void Hello_Click(object sender,RoutedEventArgs e)
    {
        if(!_hello.IsEnabled||_session is null)return;
        if(_session.HelloEnabled)
        {
            if(MessageBox.Show(Window.GetWindow(this),"Stäng av Windows Hello för valvet på den här datorn?","Windows Hello",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
            try{_session.DisableHello();Status("Windows Hello är avstängt för valvet.");}catch(Exception error)when(error is IOException or UnauthorizedAccessException){Status(error.Message);}
            Update();return;
        }
        if(!WindowsHello.IsAvailable()){MessageBox.Show(Window.GetWindow(this),"Windows Hello finns inte eller är inte inställt.\n\nSlå på det i Windows: Inställningar → Konton → Inloggningsalternativ → PIN (Windows Hello), och gärna fingeravtryck eller ansikte.","Windows Hello",MessageBoxButton.OK,MessageBoxImage.Information);return;}
        if(MessageBox.Show(Window.GetWindow(this),"Med Windows Hello kan du låsa upp valvet och godkänna visa, kopiera och fylla i lösenord med fingeravtryck, ansikte eller Windows-PIN i stället för att skriva huvudlösenordet.\n\n"+
            "• Nyckeln är bunden till den här datorn och ditt Windows-konto (säkerhetschipet där det finns) och fungerar inte på en annan dator.\n"+
            "• Ett annat program kan inte godkänna åt dig; Windows visar alltid sin egen ruta.\n"+
            "• Efter omstart av datorn krävs huvudlösenordet första gången.\n"+
            "• Huvudlösenordet fungerar alltid, och behövs om du flyttar valvet.\n\nSlå på Windows Hello?","Windows Hello",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.Yes)!=MessageBoxResult.Yes)return;
        var master=AskMaster("Ange huvudlösenordet för att slå på Windows Hello. Därefter visar Windows sin egen ruta.");if(master is null)return;var wrong=false;
        try{await RunAsync(async cancellation=>{try{Status("Bekräfta med Windows Hello…");await _session.EnableHelloAsync(master,Handle,cancellation);Status("Windows Hello är påslaget för valvet på den här datorn.");}catch(UnauthorizedAccessException){wrong=true;}});}
        finally{Array.Clear(master);}
        if(wrong)WrongMaster();
    }
    private async void Remove_Click(object sender,RoutedEventArgs e)
    {
        if(!_remove.IsEnabled||_files.SelectedItem is not VaultEntry entry)return;
        if(MessageBox.Show(Window.GetWindow(this),$"Ta bort ”{entry.Name}” ur valvet?\n\nDen krypterade kopian raderas. Originalfiler utanför valvet påverkas inte. Spara en dekrypterad kopia först om du behöver filen.","Ta bort ur valvet",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        await RunAsync(async cancellation=>{await _session!.DeleteAsync(entry.Id,cancellation);Status($"”{entry.Name}” är borttagen ur valvet.");});
    }
    private async void Export_Click(object sender,RoutedEventArgs e)
    {
        if(!_export.IsEnabled||_files.SelectedItem is not VaultEntry entry)return;var dialog=new SaveFileDialog{Title="Spara dekrypterad kopia utanför valvet",FileName=entry.Name,Filter="Alla filer|*.*",OverwritePrompt=true};if(dialog.ShowDialog(Window.GetWindow(this))!=true)return;
        await RunAsync(async cancellation=>{Status("Verifierar och dekrypterar valvfilen…");var result=await _session!.ExportAsync(entry.Id,dialog.FileName,cancellation);Status("Dekrypterad kopia sparad: "+result.OutputPath+(result.CleanupWarning is null?"":"\n"+result.CleanupWarning));});
    }
}
