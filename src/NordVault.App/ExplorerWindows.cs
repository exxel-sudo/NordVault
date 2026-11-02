using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NordVault.Core;

namespace NordVault.App;

/// <summary>Right-click a folder or file → NordVault → Skapa självuppackande arkiv. Needs no vault: the archive has its own password.</summary>
internal sealed class ArchiveWindow : Window
{
    private readonly string _source;
    private readonly PasswordBox _password=new(),_repeat=new();
    private readonly TextBlock _summary=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,2,0,14),Foreground=Brushes.DimGray};
    private readonly TextBlock _hint=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,12),FontSize=12};
    private readonly TextBlock _target=new(){TextWrapping=TextWrapping.Wrap,VerticalAlignment=VerticalAlignment.Center};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,0)};
    private readonly ProgressBar _progress=new(){Height=8,IsIndeterminate=true,Visibility=Visibility.Collapsed,Margin=new Thickness(0,12,0,0)};
    private readonly Button _create=new(){Content="Skapa arkivet",IsEnabled=false},_change=new(){Content="Ändra…"},_show=new(){Content="Visa i Utforskaren",Visibility=Visibility.Collapsed},_close=new(){Content="Stäng"};
    private readonly GnuPgRuntime _runtime=new(Path.Combine(AppContext.BaseDirectory,"tools","gnupg"));
    private string _output;
    private bool _counted;
    private CancellationTokenSource? _cancel;

    public ArchiveWindow(string source)
    {
        _source=Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        Title="NordVault · Självuppackande arkiv";Width=640;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        _create.Style=(Style)Application.Current.FindResource("Primary");
        _output=DefaultOutput(_source);
        var panel=new StackPanel{Margin=new Thickness(26)};
        panel.Children.Add(new TextBlock{Text="Skapa självuppackande arkiv",FontSize=23,FontWeight=FontWeights.SemiBold});
        panel.Children.Add(new TextBlock{Text=(Directory.Exists(_source)?"Mapp: ":"Fil: ")+_source,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,0),FontWeight=FontWeights.SemiBold});
        panel.Children.Add(_summary);
        panel.Children.Add(new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12),Text=
            "Allt krypteras med AES-256 till en .exe-fil. Den som dubbelklickar på filen får ange lösenordet och kan sedan packa upp – på vilken Windows-dator som helst, utan NordVault."});
        panel.Children.Add(new TextBlock{Text="Arkivets lösenord (minst 16 tecken)"});panel.Children.Add(_password);
        panel.Children.Add(new TextBlock{Text="Skriv lösenordet igen",Margin=new Thickness(0,8,0,0)});panel.Children.Add(_repeat);
        panel.Children.Add(_hint);
        var target=new DockPanel{Margin=new Thickness(0,0,0,4)};
        DockPanel.SetDock(_change,Dock.Right);target.Children.Add(_change);target.Children.Add(_target);panel.Children.Add(target);
        panel.Children.Add(new Border{Background=new SolidColorBrush(Color.FromRgb(0xFF,0xF2,0xDB)),CornerRadius=new CornerRadius(8),Padding=new Thickness(12),Margin=new Thickness(0,10,0,0),Child=new TextBlock{TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.FromRgb(0x73,0x55,0x1D)),FontSize=12,Text=
            "Glömmer du lösenordet går arkivet inte att öppna – det finns ingen bakdörr. Originalen ligger kvar; radera dem själv om de inte ska finnas kvar okrypterade. Skicka lösenordet på ett annat sätt än filen."}});
        panel.Children.Add(_progress);panel.Children.Add(_status);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};
        buttons.Children.Add(_show);buttons.Children.Add(_close);buttons.Children.Add(_create);panel.Children.Add(buttons);
        Content=panel;
        _password.PasswordChanged+=(_,_)=>Check();_repeat.PasswordChanged+=(_,_)=>Check();
        _change.Click+=(_,_)=>
        {
            var save=new SaveFileDialog{Title="Spara självuppackande arkiv",InitialDirectory=Path.GetDirectoryName(_output),FileName=Path.GetFileName(_output),Filter="Självuppackande arkiv (*.exe)|*.exe",AddExtension=true,OverwritePrompt=false};
            if(save.ShowDialog(this)!=true)return;
            if(File.Exists(save.FileName)){_status.Text="Filen finns redan. Välj ett annat namn – ingenting skrivs över.";return;}
            _output=save.FileName;Check();
        };
        _create.Click+=async(_,_)=>await CreateAsync();
        _close.Click+=(_,_)=>Close();
        _show.Click+=(_,_)=>Process.Start(new ProcessStartInfo("explorer.exe",$"/select,\"{_output}\""){UseShellExecute=false});
        Closing+=(_,e)=>
        {
            if(_cancel is null)return;
            if(MessageBox.Show(this,"Arkivet skapas fortfarande. Avbryta?","NordVault",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)_cancel.Cancel();
            e.Cancel=true;
        };
        Closed+=(_,_)=>{_password.Clear();_repeat.Clear();};
        Loaded+=async(_,_)=>{_password.Focus();await CountAsync();};
        Check();
    }

    internal static string DefaultOutput(string source)
    {
        var folder=Path.GetDirectoryName(source)??source;
        var name=Directory.Exists(source)?Path.GetFileName(source):Path.GetFileNameWithoutExtension(source);
        var candidate=Path.Combine(folder,name+" - krypterat.exe");
        for(var n=2;File.Exists(candidate)||Directory.Exists(candidate);n++)candidate=Path.Combine(folder,$"{name} - krypterat ({n}).exe");
        return candidate;
    }

    private async Task CountAsync()
    {
        var source=_source;
        try
        {
            var (files,bytes)=await Task.Run(()=>
            {
                if(File.Exists(source))return(1,new FileInfo(source).Length);
                var options=new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint|FileAttributes.Device|FileAttributes.Offline};
                int count=0;long size=0;foreach(var f in new DirectoryInfo(source).EnumerateFiles("*",options)){count++;size+=f.Length;if(count>5000)break;}
                return(count,size);
            });
            _summary.Text=files>5000?"Mappen har fler än 5 000 filer. Dela upp den i mindre mappar.":files==0?"Mappen är tom.":$"{files} {(files==1?"fil":"filer")} · {Size(bytes)}";
            _counted=files is > 0 and <= 5000;
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){_summary.Text="Kunde inte läsa: "+error.Message;}
        Check();
    }

    private void Check()
    {
        _target.Text="Sparas som: "+_output;
        var length=_password.Password.Length;
        _hint.Text=length==0?"":length<16?$"✗ {length} av minst 16 tecken.":_repeat.Password.Length==0?"Skriv lösenordet igen.":_password.Password!=_repeat.Password?"✗ Lösenorden är inte lika.":"✓ Lösenordet är godkänt.";
        _hint.Foreground=_hint.Text.StartsWith('✓')?Brushes.DarkGreen:Brushes.Firebrick;
        var reader=Path.Combine(AppContext.BaseDirectory,"NordVault.Reader.exe");
        var ready=_runtime.IsInstalled&&File.Exists(reader)&&FileCryptoPanel.ArchiveTemplateHash() is not null;
        if(!ready&&_cancel is null)_status.Text="Arkivfunktionen saknas i den här NordVault-mappen (krypteringsmotorn eller läsaren). Installera om NordVault.";
        _create.IsEnabled=ready&&_counted&&_cancel is null&&length>=16&&_password.Password==_repeat.Password;
        _change.IsEnabled=_password.IsEnabled=_repeat.IsEnabled=_cancel is null;
    }

    private async Task CreateAsync()
    {
        if(!_create.IsEnabled)return;
        var password=UiSecrets.Read(_password);
        _cancel=new CancellationTokenSource();_progress.Visibility=Visibility.Visible;_show.Visibility=Visibility.Collapsed;Check();
        try
        {
            FileCryptoService.ValidatePassword(password,false);
            var progress=new Progress<FileCryptoProgress>(p=>{if(_cancel is not null)_status.Text=p.Stage+"…";});
            await new ArchivePackage(_runtime).CreateAsync([_source],_output,password,Path.Combine(AppContext.BaseDirectory,"NordVault.Reader.exe"),FileCryptoPanel.ArchiveTemplateHash()!,_cancel.Token,progress);
            _status.Text="✓ Klart! Arkivet är sparat:\n"+_output+"\n\nDubbelklicka på filen för att packa upp. Originalen finns kvar.";
            _show.Visibility=Visibility.Visible;_password.Clear();_repeat.Clear();_close.Content="Klar";_counted=false;
        }
        catch(OperationCanceledException){_status.Text="Avbrutet. Inget arkiv sparades och originalen är orörda.";}
        catch(Exception error)when(error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {_status.Text=error is UnauthorizedAccessException?"Windows nekade åtkomst till filerna eller mappen där arkivet ska sparas.":error.Message;}
        finally{Array.Clear(password);_cancel.Dispose();_cancel=null;_progress.Visibility=Visibility.Collapsed;Check();}
    }

    internal static string Size(long bytes)=>bytes>=1L<<30?$"{bytes/(double)(1L<<30):0.0} GB":bytes>=1L<<20?$"{bytes/(double)(1L<<20):0.0} MB":$"{Math.Max(1,bytes/1024)} kB";
}

/// <summary>Right-click a drive → NordVault → Rensa ledigt utrymme. Works without BitLocker; never touches existing files.</summary>
internal sealed class WipeFreeSpaceWindow : Window
{
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(uint flags);
    private readonly DriveInfo _drive;
    private readonly RadioButton _one=new(){Content="1 pass – slumpdata (rekommenderas för USB-minnen och SSD)",IsChecked=true,Margin=new Thickness(0,4,0,0)};
    private readonly RadioButton _three=new(){Content="3 pass – 00, FF, slumpdata",Margin=new Thickness(0,4,0,0)};
    private readonly RadioButton _seven=new(){Content="7 pass – DoD 5220.22-M ECE (bara för gamla snurrande hårddiskar)",Margin=new Thickness(0,4,0,0)};
    private readonly TextBlock _info=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,12),Foreground=Brushes.DimGray};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,0)};
    private readonly ProgressBar _progress=new(){Height=10,Maximum=1000,Visibility=Visibility.Collapsed,Margin=new Thickness(0,14,0,0)};
    private readonly Button _start=new(){Content="Starta rensning"},_close=new(){Content="Stäng"};
    private CancellationTokenSource? _cancel;

    public WipeFreeSpaceWindow(string root)
    {
        _drive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
        Title="NordVault · Rensa ledigt utrymme";Width=640;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        _start.Style=(Style)Application.Current.FindResource("Primary");
        var panel=new StackPanel{Margin=new Thickness(26)};
        panel.Children.Add(new TextBlock{Text="Rensa ledigt utrymme",FontSize=23,FontWeight=FontWeights.SemiBold});
        panel.Children.Add(_info);
        panel.Children.Add(new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12),Text=
            "Skriver över platsen där raderade filer har legat, så att de inte kan tas fram med återställningsprogram. Dina befintliga filer rörs inte. Disken är full medan rensningen pågår – ta inte ut den."});
        panel.Children.Add(new TextBlock{Text="Antal pass",FontWeight=FontWeights.SemiBold});
        panel.Children.Add(_one);panel.Children.Add(_three);panel.Children.Add(_seven);
        panel.Children.Add(new Border{Background=new SolidColorBrush(Color.FromRgb(0xFF,0xF2,0xDB)),CornerRadius=new CornerRadius(8),Padding=new Thickness(12),Margin=new Thickness(0,14,0,0),Child=new TextBlock{TextWrapping=TextWrapping.Wrap,Foreground=new SolidColorBrush(Color.FromRgb(0x73,0x55,0x1D)),FontSize=12,Text=
            "USB-minnen och SSD flyttar själva runt var data skrivs. Därför ger fler pass ingen extra säkerhet där, och ingen rensning kan garantera att allt är borta. Säkrast är att kryptera disken från början (BitLocker To Go). Kopior på andra ställen rensas inte."}});
        panel.Children.Add(_progress);panel.Children.Add(_status);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,16,0,0)};
        buttons.Children.Add(_close);buttons.Children.Add(_start);panel.Children.Add(buttons);
        Content=panel;
        _start.Click+=async(_,_)=>{if(_cancel is null)await StartAsync();else _cancel.Cancel();};
        _close.Click+=(_,_)=>Close();
        Closing+=(_,e)=>
        {
            if(_cancel is null)return;e.Cancel=true;
            if(MessageBox.Show(this,"Rensningen pågår. Avbryta? Utfyllnadsfilerna tas bort och disken blir som förut.","NordVault",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)_cancel.Cancel();
        };
        Describe();
    }

    private int Passes=>_seven.IsChecked==true?7:_three.IsChecked==true?3:1;

    private void Describe()
    {
        var refuse=FreeSpaceWiper.Refuse(_drive);
        string what;
        try{what=_drive.IsReady?$"{_drive.RootDirectory.FullName}  {(_drive.VolumeLabel is {Length:>0} label?"\""+label+"\" ":"")}· {(_drive.DriveType==DriveType.Removable?"USB/flyttbar disk":"lokal disk")} · {_drive.DriveFormat} · {ArchiveWindow.Size(_drive.AvailableFreeSpace)} ledigt av {ArchiveWindow.Size(_drive.TotalSize)}":_drive.Name;}
        catch(IOException){what=_drive.Name;}
        _info.Text=what;
        if(refuse is not null){_status.Text="✗ "+refuse;_status.Foreground=Brushes.Firebrick;}
        _start.IsEnabled=refuse is null;
    }

    private async Task StartAsync()
    {
        Describe();if(!_start.IsEnabled)return;
        var passes=Passes;
        if(MessageBox.Show(this,$"Rensa ledigt utrymme på {_drive.Name} med {passes} pass?\n\nDisken fylls helt under tiden och kan vara långsam. Ta inte ut den förrän rensningen är klar. Befintliga filer behålls.",
            "NordVault",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
        _cancel=new CancellationTokenSource();_start.Content="Avbryt";_close.IsEnabled=_one.IsEnabled=_three.IsEnabled=_seven.IsEnabled=false;
        _progress.Visibility=Visibility.Visible;_progress.Value=0;_status.Foreground=Brushes.Black;_status.Text="Startar…";
        SetThreadExecutionState(0x80000001); // keep the PC awake (ES_CONTINUOUS | ES_SYSTEM_REQUIRED)
        var started=Stopwatch.StartNew();
        try
        {
            var progress=new Progress<WipeProgress>(p=>
            {
                if(_cancel is null)return;
                var share=Math.Clamp(p.Written/(double)Math.Max(1,p.Total),0,1);_progress.Value=share*1000;
                var speed=p.Written/Math.Max(1,started.Elapsed.TotalSeconds);
                var left=speed>0&&share>0.01?TimeSpan.FromSeconds(Math.Max(0,p.Total-p.Written)/speed):(TimeSpan?)null;
                _status.Text=$"Pass {p.Pass} av {p.Passes} · {p.Stage} · {ArchiveWindow.Size(p.Written)} av ca {ArchiveWindow.Size(p.Total)} · {ArchiveWindow.Size((long)speed)}/s"+(left is {} t?$" · ca {(t.TotalHours>=1?$"{(int)t.TotalHours} h {t.Minutes} min":$"{Math.Max(1,t.Minutes)} min")} kvar":"");
            });
            await Task.Run(()=>FreeSpaceWiper.WipeAsync(_drive.RootDirectory.FullName,passes,progress,_cancel.Token));
            _progress.Value=1000;_status.Foreground=Brushes.DarkGreen;
            _status.Text=$"✓ Klart! Ledigt utrymme på {_drive.Name} är överskrivet med {passes} pass ({started.Elapsed:h\\:mm\\:ss}). Utfyllnadsfilerna är borttagna.";
        }
        catch(OperationCanceledException){_status.Text="Avbrutet. Utfyllnadsfilerna är borttagna. Det som hann skrivas över är överskrivet.";}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException)
        {_status.Foreground=Brushes.Firebrick;_status.Text=(error is UnauthorizedAccessException?"Windows nekade skrivning på disken. Är den skrivskyddad?":"Rensningen stoppades: "+error.Message)+" Utfyllnadsfilerna har tagits bort.";}
        finally
        {
            SetThreadExecutionState(0x80000000);
            _cancel.Dispose();_cancel=null;_start.Content="Starta rensning";_close.IsEnabled=_one.IsEnabled=_three.IsEnabled=_seven.IsEnabled=true;
            var text=_status.Text;var color=_status.Foreground;Describe();_status.Text=text;_status.Foreground=color;
        }
    }
}
