using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NordVault.Core;

namespace NordVault.Setup;

internal sealed class SetupWindow : Window
{
    private readonly RadioButton _installed=new(){Content="Installera på datorn",IsChecked=true,GroupName="mode"};
    private readonly RadioButton _portable=new(){Content="Portabel version för USB",GroupName="mode"};
    private readonly CheckBox _allUsers=new(){Content="Alla användare — kräver administratör"};
    private readonly CheckBox _desktop=new(){Content="Genväg på skrivbordet",IsChecked=true};
    private readonly CheckBox _startMenu=new(){Content="Startmeny med program och avinstallation",IsChecked=true};
    private readonly TextBox _folder=new(){Padding=new Thickness(12,10,12,10),FontSize=13};
    private readonly Button _choose=new(){Content="Bläddra…"};
    private readonly Button _start=new(){Content="Installera NordVault"};
    private readonly Button _close=new(){Content="Stäng"};
    private readonly Button _remove=new(){Content="Avinstallera befintlig installation…",Visibility=Visibility.Collapsed};
    private readonly Button _launch=new(){Content="Starta NordVault",Visibility=Visibility.Collapsed};
    private readonly Button _openFolder=new(){Content="Öppna mappen",Visibility=Visibility.Collapsed};
    private readonly TextBlock _summary=new(){TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap};
    private readonly ProgressBar _progress=new(){Height=5,Minimum=0,Maximum=100,Visibility=Visibility.Collapsed};
    private readonly TextBlock _heading=new(){FontSize=29,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap};
    private readonly StackPanel _choices=new();
    private string? _uninstall;
    private readonly bool _helper;
    private bool _busy,_finished,_initializing=true,_helperTransferred,_retryRegistration;
    private bool _refreshingSummary,_wasPortable,_savedDesktop=true,_savedMenu=true;
    private string Mode=>_portable.IsChecked==true?"Portable":_allUsers.IsChecked==true?"AllUsers":"User";
    private static SolidColorBrush Brush(string hex)=>new((Color)ColorConverter.ConvertFromString(hex));

    public SetupWindow(string[] args)
    {
        _uninstall=Argument(args,"--uninstall");_helper=args.Contains("--helper");
        Title="NordVault Setup · "+PackageResources.Version;Width=940;Height=Math.Min(800,SystemParameters.WorkArea.Height-36);MinWidth=860;MinHeight=600;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        FontFamily=new FontFamily("Segoe UI");FontSize=14;Foreground=Brush("#17263E");Background=Brush("#F3F6FA");
        AddStyles();
        var root=new Grid{Background=Background};root.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(228)});root.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar=new Grid{Margin=new Thickness(26,35,24,28)};sidebar.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});sidebar.RowDefinitions.Add(new RowDefinition());sidebar.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var brand=new StackPanel();brand.Children.Add(Text("◈",45,"#64CBB3"));brand.Children.Add(Text("NordVault",27,"#FFFFFF",true));brand.Children.Add(Text("Dina data. Ditt skydd.",12,"#A7B7CA"));sidebar.Children.Add(brand);
        var steps=new StackPanel{Margin=new Thickness(0,40,0,0)};
        steps.Children.Add(Text("01  Välj hur du installerar",13,"#FFFFFF",true));steps.Children.Add(Text("02  Mapp och genvägar",13,"#C0CFDE"));steps.Children.Add(Text("03  Klart att använda",13,"#C0CFDE"));
        steps.Children.Add(new TextBlock{Text="Öppna programmet direkt.\nSkapa ett personligt valv\nom du behöver det.",Foreground=Brush("#A7B7CA"),FontSize=13,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,35,0,0)});Grid.SetRow(steps,1);sidebar.Children.Add(steps);
        var version=Text("VERSION "+PackageResources.Version+"\nWindows 11 · x64",11,"#A7B7CA");Grid.SetRow(version,2);sidebar.Children.Add(version);
        root.Children.Add(new Border{Background=Brush("#16283F"),Child=sidebar});
        var content=new Grid{Margin=new Thickness(30,29,30,20)};content.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});content.RowDefinitions.Add(new RowDefinition());content.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});Grid.SetColumn(content,1);root.Children.Add(content);
        var intro=new StackPanel();_heading.Text=_uninstall is null?"Välkommen till NordVault":"Avinstallera NordVault";intro.Children.Add(_heading);
        intro.Children.Add(new TextBlock{Text=_uninstall is null?"Diskar, USB och filer. Installera eller ta med på USB.":"Programfiler och tillhörande genvägar tas bort.",Foreground=Brush("#64748B"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,20)});content.Children.Add(intro);
        var form=new StackPanel();
        var modes=new StackPanel();modes.Children.Add(_installed);modes.Children.Add(new TextBlock{Text="AppData som standard. Startmeny och skrivbordsgenväg.",Foreground=Brush("#64748B"),FontSize=12,Margin=new Thickness(25,2,0,12)});modes.Children.Add(_portable);modes.Children.Add(new TextBlock{Text="Inga registerposter eller genvägar. Hela mappen följer med.",Foreground=Brush("#64748B"),FontSize=12,Margin=new Thickness(25,2,0,10)});modes.Children.Add(_allUsers);
        _choices.Children.Add(Card(modes));form.Children.Add(_choices);
        form.Children.Add(Text("INSTALLATIONSMAPP",11,"#64748B",true));
        var folderRow=new Grid{Margin=new Thickness(0,5,0,14)};folderRow.ColumnDefinitions.Add(new ColumnDefinition());folderRow.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});folderRow.Children.Add(_folder);Grid.SetColumn(_choose,1);_choose.Margin=new Thickness(10,0,0,0);folderRow.Children.Add(_choose);form.Children.Add(folderRow);
        var links=new StackPanel();links.Children.Add(_desktop);links.Children.Add(_startMenu);form.Children.Add(Card(links));
        form.Children.Add(new Border{Background=Brush("#E4F0EC"),CornerRadius=new CornerRadius(9),Padding=new Thickness(15),Child=_summary,Margin=new Thickness(0,2,0,10)});
        form.Children.Add(_remove);
        var scroller=new ScrollViewer{Content=form,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(scroller,1);content.Children.Add(scroller);
        var footer=new StackPanel();footer.Children.Add(_progress);_status.Margin=new Thickness(0,10,0,10);footer.Children.Add(_status);
        var buttons=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};_start.Style=(Style)Resources["PrimaryButton"];_launch.Style=(Style)Resources["PrimaryButton"];
        buttons.Children.Add(_openFolder);buttons.Children.Add(_launch);buttons.Children.Add(_start);buttons.Children.Add(_close);footer.Children.Add(buttons);Grid.SetRow(footer,2);content.Children.Add(footer);Content=root;
        _folder.Text=_uninstall??Argument(args,"--manage")??Argument(args,"--target")??DefaultFolder();
        var requestedMode=Argument(args,"--mode");
        if(requestedMode is null&&File.Exists(Path.Combine(_folder.Text,DeploymentService.StateFile)))try{requestedMode=DeploymentService.ReadState(_folder.Text).Mode;}catch(Exception error)when(error is IOException or JsonException or UnauthorizedAccessException){}
        _portable.IsChecked=requestedMode=="Portable";_allUsers.IsChecked=requestedMode=="AllUsers";
        if(Argument(args,"--desktop")=="false")_desktop.IsChecked=false;if(Argument(args,"--start-menu")=="false")_startMenu.IsChecked=false;
        _installed.Checked+=(_,_)=>ModeChanged();_portable.Checked+=(_,_)=>ModeChanged();_allUsers.Checked+=(_,_)=>ModeChanged();_allUsers.Unchecked+=(_,_)=>ModeChanged();
        _folder.TextChanged+=(_,_)=>RefreshSummary();_desktop.Checked+=(_,_)=>RefreshSummary();_desktop.Unchecked+=(_,_)=>RefreshSummary();_startMenu.Checked+=(_,_)=>RefreshSummary();_startMenu.Unchecked+=(_,_)=>RefreshSummary();
        _choose.Click+=Choose_Click;_start.Click+=Start_Click;_close.Click+=(_,_)=>Close();
        _remove.Click+=(_,_)=>LaunchUninstallWindow();_launch.Click+=(_,_)=>{if(LaunchProgram())Close();};_openFolder.Click+=(_,_)=>Process.Start(new ProcessStartInfo(_folder.Text){UseShellExecute=true});
        Closing+=(_,e)=>{if(_busy)e.Cancel=true;};if(_helper)Closed+=(_,_)=>{if(!_helperTransferred)ScheduleHelperCleanup();};
        _initializing=false;RefreshSummary();
        if(_uninstall is not null){_choices.Visibility=Visibility.Collapsed;_desktop.Visibility=_startMenu.Visibility=Visibility.Collapsed;_folder.IsReadOnly=true;_choose.Visibility=Visibility.Collapsed;_start.Content="Avinstallera";_remove.Visibility=Visibility.Collapsed;}
        if(args.Contains("--approved"))Loaded+=(_,_)=>Start_Click(this,new RoutedEventArgs());
    }
    private void AddStyles()
    {
        var style=new Style(typeof(Button));style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(15,10,15,10)));style.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(0,0,8,0)));style.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.White));style.Setters.Add(new Setter(Control.ForegroundProperty,Brush("#17263E")));style.Setters.Add(new Setter(Control.BorderBrushProperty,Brush("#D8E0E8")));style.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(1)));style.Setters.Add(new Setter(Control.FontWeightProperty,FontWeights.SemiBold));
        var template=new ControlTemplate(typeof(Button));var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(8));border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));border.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));border.SetValue(Border.BorderThicknessProperty,new TemplateBindingExtension(Control.BorderThicknessProperty));border.SetValue(Border.PaddingProperty,new TemplateBindingExtension(Control.PaddingProperty));var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);presenter.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(presenter);template.VisualTree=border;style.Setters.Add(new Setter(Control.TemplateProperty,template));
        var hover=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(UIElement.OpacityProperty,0.84));style.Triggers.Add(hover);var disabled=new Trigger{Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,0.45));style.Triggers.Add(disabled);Resources.Add(typeof(Button),style);
        var primary=new Style(typeof(Button),style);primary.Setters.Add(new Setter(Control.BackgroundProperty,Brush("#14695B")));primary.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.White));primary.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0)));Resources.Add("PrimaryButton",primary);
        foreach(var type in new[]{typeof(RadioButton),typeof(CheckBox)}){var option=new Style(type);option.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(0,6,0,6)));Resources.Add(type,option);}
    }
    private static TextBlock Text(string value,double size,string color,bool bold=false)=>new(){Text=value,FontSize=size,Foreground=Brush(color),FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,7)};
    private static Border Card(UIElement child)=>new(){Background=Brushes.White,BorderBrush=Brush("#DFE6ED"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(10),Padding=new Thickness(17,10,17,10),Margin=new Thickness(0,0,0,14),Child=child};
    private static string? Argument(string[] args,string name){var index=Array.IndexOf(args,name);return index>=0&&index+1<args.Length?args[index+1]:null;}
    private string DefaultFolder()=>_portable.IsChecked==true?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"NordVault-Portable"):_allUsers.IsChecked==true?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"NordVault"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","NordVault");
    private void ModeChanged(){if(_initializing||_busy||_refreshingSummary)return;if(_portable.IsChecked==true)_allUsers.IsChecked=false;_folder.Text=DefaultFolder();RefreshSummary();}
    private void RefreshSummary()
    {
        if(_initializing||_finished||_refreshingSummary)return;
        _refreshingSummary=true;
        var portable=Mode=="Portable";_allUsers.IsEnabled=!portable;_desktop.IsEnabled=_startMenu.IsEnabled=!portable;
        if(portable&&!_wasPortable){_savedDesktop=_desktop.IsChecked==true;_savedMenu=_startMenu.IsChecked==true;_desktop.IsChecked=_startMenu.IsChecked=false;}
        else if(!portable&&_wasPortable){_desktop.IsChecked=_savedDesktop;_startMenu.IsChecked=_savedMenu;}
        _wasPortable=portable;
        InstallationState? existing=null;
        try{if(File.Exists(Path.Combine(_folder.Text,DeploymentService.StateFile)))existing=DeploymentService.ReadState(_folder.Text);}catch(Exception error)when(error is IOException or JsonException or UnauthorizedAccessException or ArgumentException){}
        _start.Content=_uninstall is not null?"Avinstallera":existing is null?portable?"Packa upp portabelt":"Installera NordVault":existing.Version==PackageResources.Version?"Reparera installationen":"Uppdatera till "+PackageResources.Version;
        _remove.Visibility=existing is not null&&_uninstall is null?Visibility.Visible:Visibility.Collapsed;
        _summary.Text=_uninstall is not null?"Ditt valv och egna filer behålls. Endast verifierade programfiler och tillhörande genvägar tas bort.":existing is not null?$"NordVault {existing.Version} finns här. Uppdatering/reparation bevarar valv och egna filer. Stäng NordVault före start.":portable?"Portabelt läge: inga genvägar eller registerposter. Starta NordVault.exe från mappen.":"Programmet öppnas direkt efter installation. Valvet är valfritt. Avinstallation finns i Windows Installerade appar och i programmappen.";
        _refreshingSummary=false;
    }
    private void Choose_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFolderDialog{Title="Välj NordVault-mapp"};if(dialog.ShowDialog(this)!=true)return;
        var selected=dialog.FolderName;_folder.Text=Directory.EnumerateFileSystemEntries(selected).Any()&&!File.Exists(Path.Combine(selected,DeploymentService.StateFile))?Path.Combine(selected,"NordVault"):selected;
    }
    private static bool Admin(){using var identity=WindowsIdentity.GetCurrent();return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);}
    private void SetBusy(bool busy)
    {
        _busy=busy;_start.IsEnabled=_close.IsEnabled=_remove.IsEnabled=!busy;_folder.IsEnabled=_choose.IsEnabled=_choices.IsEnabled=_desktop.IsEnabled=_startMenu.IsEnabled=!busy&&!_finished;
        _progress.Visibility=busy?Visibility.Visible:_finished?Visibility.Visible:Visibility.Collapsed;_progress.IsIndeterminate=busy;if(!busy&&_finished)_progress.Value=100;
        if(!busy&&!_finished)RefreshSummary();
    }
    private async void Start_Click(object sender,RoutedEventArgs e)
    {
        if(_busy||(_finished&&!_retryRegistration))return;string folder;
        try{folder=DeploymentService.ValidateDestination(_folder.Text);}catch(Exception error)when(error is IOException or ArgumentException){Fail(error.Message);return;}
        if(!_helper&&Path.GetDirectoryName(Environment.ProcessPath!)!.Equals(folder,StringComparison.OrdinalIgnoreCase)){RunTemporaryWorker(folder);return;}
        InstallationState? previous=null;
        try{if(File.Exists(Path.Combine(folder,DeploymentService.StateFile)))previous=DeploymentService.ReadState(folder);}catch(Exception error)when(error is IOException or JsonException or UnauthorizedAccessException){Fail("Installationsinformationen kunde inte läsas: "+error.Message);return;}
        var mode=_uninstall is not null?previous?.Mode??Mode:Mode;
        var needsAdmin=mode=="AllUsers"||new[]{Environment.SpecialFolder.ProgramFiles,Environment.SpecialFolder.ProgramFilesX86}.Any(s=>folder.StartsWith(Environment.GetFolderPath(s).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase));
        if(needsAdmin&&!Admin()){Relaunch(folder);return;}
        if(previous is not null)
        {
            var running=RunningApp.Find(folder);
            if(running.Windows.Length>0&&MessageBox.Show(this,"NordVault är öppet och stängs för att "+(_uninstall is not null?"avinstallationen":"uppdateringen")+" ska kunna göras. Valvet låses. Pågående filåtgärder i NordVault måste vara klara.\n\nStänga NordVault nu?","NordVault Setup",MessageBoxButton.OKCancel,MessageBoxImage.Information,MessageBoxResult.OK)!=MessageBoxResult.OK)return;
            foreach(var process in running.Windows.Concat(running.Relays))process.Dispose();
        }
        SetBusy(true);_status.Foreground=Brush("#64748B");
        try
        {
            if(previous is not null)
            {
                _status.Text="Stänger NordVault och kopplingen till Firefox…";
                if(await RunningApp.CloseAsync(folder) is {} stillRunning)throw new IOException(stillRunning);
            }
            if(_uninstall is not null)
            {
                if(previous is null)throw new IOException("Ingen verifierad NordVault-installation finns i mappen.");
                var old=PackageResources.Previous(previous);_status.Text="Tar bort programfiler och genvägar. Egna filer behålls…";
                var result=await Task.Run(()=>new DeploymentService(old.Manifest).Uninstall(folder,Path.Combine(folder,"NordVault.Setup.exe")));
                InstallationRegistration.Unregister(folder,mode);
                _status.Text=$"Avinstallation klar. {result.RemovedFiles} programfiler borttagna. Valv och egna filer behölls."+(result.PreservedFiles>0?$"\n{result.PreservedFiles} ändrade programfiler behölls.":"");
                _finished=true;_heading.Text="NordVault är avinstallerat";_start.Visibility=Visibility.Collapsed;
            }
            else
            {
                if(!_retryRegistration)
                {
                    _status.Text=previous is null?"Packar upp och verifierar programfiler…":"Verifierar och uppdaterar programfiler…";
                    var service=new DeploymentService(PackageResources.Manifest());
                    var old=previous is not null?PackageResources.Previous(previous):default;
                    await Task.Run(async()=>{using var payload=PackageResources.Payload();if(previous is null)await service.InstallAsync(payload,folder,mode,Environment.ProcessPath!);else await service.UpdateAsync(payload,folder,mode,Environment.ProcessPath!,old.Manifest,old.SetupHash);});
                }
                _finished=true;
                try
                {
                    _status.Text="Skapar och verifierar valda genvägar…";InstallationRegistration.Register(folder,mode,_desktop.IsChecked==true,_startMenu.IsChecked==true);
                    _retryRegistration=false;_heading.Text="Du är redo att börja";_status.Foreground=Brush("#14695B");_status.Text=mode=="Portable"?"Portabel version klar. Öppna NordVault direkt — valvet är valfritt.":"Installationen är klar. Valda genvägar och Windows avinstallationspost är skapade.";_start.Visibility=Visibility.Collapsed;
                }
                catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
                {_retryRegistration=true;_status.Foreground=Brush("#9A5A12");_status.Text="Programfilerna är klara, men genvägar/register kunde inte slutföras: "+error.Message;_start.Content="Försök skapa genvägar igen";}
                _launch.Visibility=_openFolder.Visibility=Visibility.Visible;_remove.Visibility=Visibility.Collapsed;
            }
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
        {
            // A sharing violation (HRESULT 0x80070020) means a program file is still open, usually by Firefox restarting its NordVault relay.
            Fail(error is UnauthorizedAccessException?"Windows nekade åtkomst. Stäng NordVault och välj en skrivbar plats eller kör som administratör.":
                error is IOException&&(error.HResult&0xFFFF)==32?"En programfil används fortfarande, oftast av Firefox-kopplingen. Stäng Firefox och NordVault helt och klicka igen. Den tidigare versionen finns kvar.":error.Message);
        }
        finally{SetBusy(false);}
    }
    private void Fail(string message){_status.Foreground=Brush("#A53D35");_status.Text=message;}
    private bool LaunchProgram()
    {try{Process.Start(new ProcessStartInfo(Path.Combine(_folder.Text,"NordVault.exe")){UseShellExecute=true,WorkingDirectory=_folder.Text});return true;}catch(System.ComponentModel.Win32Exception error){Fail("Programmet kunde inte startas: "+error.Message);return false;}}
    private void Relaunch(string folder)
    {
        try
        {
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true,Verb="runas"};
            foreach(var arg in _uninstall is not null?new[]{"--uninstall",folder}:new[]{"--target",folder,"--mode",Mode,"--desktop",(_desktop.IsChecked==true).ToString().ToLowerInvariant(),"--start-menu",(_startMenu.IsChecked==true).ToString().ToLowerInvariant()})start.ArgumentList.Add(arg);
            if(_helper)start.ArgumentList.Add("--helper");start.ArgumentList.Add("--approved");Process.Start(start);_helperTransferred=_helper;Close();
        }
        catch(System.ComponentModel.Win32Exception){Fail("Administratörsstarten avbröts.");}
    }
    private void LaunchUninstallWindow()
    {
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=true};start.ArgumentList.Add("--uninstall");start.ArgumentList.Add(_folder.Text);Process.Start(start);Close();
    }
    private void RunTemporaryWorker(string folder)
    {
        PrivateWorkspace? work=null;
        try
        {
            work=new PrivateWorkspace();var helper=Path.Combine(work.Path,"NordVault.Setup.exe");File.Copy(Environment.ProcessPath!,helper,false);
            var start=new ProcessStartInfo(helper){UseShellExecute=true};
            foreach(var arg in _uninstall is not null?new[]{"--uninstall",folder}:new[]{"--target",folder,"--mode",Mode,"--desktop",(_desktop.IsChecked==true).ToString().ToLowerInvariant(),"--start-menu",(_startMenu.IsChecked==true).ToString().ToLowerInvariant()})start.ArgumentList.Add(arg);
            foreach(var arg in new[]{"--helper","--approved","--wait-for",Environment.ProcessId.ToString()})start.ArgumentList.Add(arg);Process.Start(start);work=null;Close();
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){Fail("Avinstalleraren kunde inte startas: "+error.Message);}
        finally{work?.Dispose();}
    }
    private static void ScheduleHelperCleanup()
    {
        var folder=Path.GetDirectoryName(Environment.ProcessPath!)!;var parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
        if(!string.Equals(Path.GetDirectoryName(folder),parent,StringComparison.OrdinalIgnoreCase)||!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(folder),"\\ANV-[0-9a-f]{32}\\z"))return;
        try
        {
            var script=Path.Combine(folder,"cleanup.ps1");
            File.WriteAllText(script,"param([string]$Folder,[int]$ParentId)\n$ErrorActionPreference='Stop'\n$target=[IO.Path]::GetFullPath($Folder)\n$root=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\\')\nif ([IO.Path]::GetDirectoryName($target) -ne $root -or [IO.Path]::GetFileName($target) -notmatch '^NV-[0-9a-f]{32}$') { exit 1 }\nwhile (Get-Process -Id $ParentId -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 500 }\nif ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { exit 1 }\nRemove-Item -LiteralPath (Join-Path $target 'NordVault.Setup.exe') -Force\nRemove-Item -LiteralPath (Join-Path $target 'cleanup.ps1') -Force\nif (-not (Get-ChildItem -LiteralPath $target -Force)) { [IO.Directory]::Delete($target,$false) }\n");
            var start=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};foreach(var arg in new[]{"-NoProfile","-ExecutionPolicy","Bypass","-File",script,"-Folder",folder,"-ParentId",Environment.ProcessId.ToString()})start.ArgumentList.Add(arg);Process.Start(start);
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { /* Public helper files may remain; never delete a wider directory. */ }
    }
}
