using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NordVault.Core;

namespace NordVault.Reader;
public sealed class App : Application
{
    [STAThread] public static void Main()=>new App().Run();
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var test=Array.IndexOf(e.Args,"--test-package");
        if(test>=0&&test+1<e.Args.Length)
        {
            // Redirected test input has a fixed encoding, independent of the Windows console code page.
            Console.InputEncoding=new System.Text.UTF8Encoding(false,true);
            try
            {
                var outputIndex=Array.IndexOf(e.Args,"--test-output");
                if(outputIndex<0||outputIndex+1>=e.Args.Length) throw new ArgumentException("Test output missing.");
                var password=(await Console.In.ReadLineAsync()??"").ToCharArray();
                try
                {
                    using var work=new PrivateWorkspace(); var runtime=await ExtractEngine(work.Path);
                    var destination=await new ArchivePackage(runtime).ExtractAsync(e.Args[test+1],e.Args[outputIndex+1],password);
                    var reportIndex=Array.IndexOf(e.Args,"--test-report");
                    if(reportIndex>=0&&reportIndex+1<e.Args.Length) File.WriteAllText(e.Args[reportIndex+1],JsonSerializer.Serialize(new{Success=true,Destination=destination}));
                }
                finally { Array.Clear(password); }
                Shutdown(0);
            }
            catch(Exception error)
            {
                var reportIndex=Array.IndexOf(e.Args,"--test-report");
                if(reportIndex>=0&&reportIndex+1<e.Args.Length) File.WriteAllText(e.Args[reportIndex+1],JsonSerializer.Serialize(new{Success=false,Error=error.GetType().Name,Message=error.Message}));
                Shutdown(1);
            }
            return;
        }
        MainWindow=new ReaderWindow(); MainWindow.Show();
    }

    internal static async Task<GnuPgRuntime> ExtractEngine(string workspace)
    {
        using var bytes=Assembly.GetExecutingAssembly().GetManifestResourceStream("NordVault.Reader.GnuPG.zip")??throw new IOException("Läsarens GnuPG-motor saknas.");
        using var zip=new ZipArchive(bytes,ZipArchiveMode.Read);
        var index=SafeZip.Index(zip,100);
        long total=0;
        foreach(var(name,entry) in index)
        {
            if(!name.StartsWith("bin/",StringComparison.Ordinal)&&name is not ("LICENSES.txt" or "ORIGIN.txt")) throw new IOException("Läsarens motorpaket har okända filer.");
            total=checked(total+entry.Length); if(total>40L*1024*1024||entry.Length>16L*1024*1024) throw new IOException("Läsarens motorpaket är för stort.");
            var destination=Path.Combine(workspace,"engine",name.Replace('/',Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input=entry.Open(); using var output=new FileStream(destination,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            await SafeZip.CopyBoundedAsync(input,output,16L*1024*1024);
        }
        var runtime=new GnuPgRuntime(Path.Combine(workspace,"engine")); runtime.Verify(); return runtime;
    }
}

internal sealed class ReaderWindow : Window
{
    private readonly TextBox _package=new(){IsReadOnly=true,Margin=new Thickness(0,6,0,12)};
    private readonly TextBox _folder=new(){IsReadOnly=true,Margin=new Thickness(0,6,0,12)};
    private readonly PasswordBox _password=new(){Padding=new Thickness(9),Margin=new Thickness(0,6,0,15)};
    private readonly Button _open=new(){Content="Lås upp och packa upp",Padding=new Thickness(17,10,17,10),Margin=new Thickness(0,8,10,0)};
    private readonly Button _cancel=new(){Content="Avbryt",IsEnabled=false,Padding=new Thickness(17,10,17,10),Margin=new Thickness(0,8,0,0)};
    private readonly TextBlock _status=new(){Text="Ingen NordVault- eller .NET-installation behövs. Filer körs aldrig automatiskt.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,20,0,0)};
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    public ReaderWindow()
    {
        Title="NordVault · öppna lösenordsskyddat arkiv"; Width=690; Height=620; MinHeight=540; FontFamily=new System.Windows.Media.FontFamily("Segoe UI"); FontSize=14; WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(243,246,249));
        var panel=new StackPanel{Margin=new Thickness(28)};
        panel.Children.Add(new TextBlock{Text="Dina filer, på den här datorn",FontSize=25,FontWeight=FontWeights.SemiBold});
        panel.Children.Add(new TextBlock{Text="Ange arkivets lösenord. Resultatet sparas i en ny mapp under den plats du väljer.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,20)});
        panel.Children.Add(new TextBlock{Text="Självuppackande NordVault-arkiv"}); panel.Children.Add(_package);
        var choose=new Button{Content="Välj annat arkiv…",Padding=new Thickness(12,7,12,7),HorizontalAlignment=HorizontalAlignment.Left}; panel.Children.Add(choose);
        panel.Children.Add(new TextBlock{Text="Spara uppackade filer under",Margin=new Thickness(0,15,0,0)});panel.Children.Add(_folder);
        var folder=new Button{Content="Välj resultatmapp…",Padding=new Thickness(12,7,12,7),HorizontalAlignment=HorizontalAlignment.Left}; panel.Children.Add(folder);
        panel.Children.Add(new TextBlock{Text="Lösenord",Margin=new Thickness(0,15,0,0)});panel.Children.Add(_password);
        var actions=new WrapPanel();actions.Children.Add(_open);actions.Children.Add(_cancel);panel.Children.Add(actions);panel.Children.Add(_status);
        Content=new ScrollViewer{Content=panel,Background=Background,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        _package.Text=Environment.ProcessPath??""; _folder.Text=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        choose.Click+=(_,_)=> { if(_busy)return;var dialog=new OpenFileDialog{Filter="NordVault-arkiv (*.exe)|*.exe|Alla filer|*.*"};if(dialog.ShowDialog(this)==true)_package.Text=dialog.FileName;};
        folder.Click+=(_,_)=> {if(_busy)return;var dialog=new OpenFolderDialog{Title="Välj en mapp för resultatet"};if(dialog.ShowDialog(this)==true)_folder.Text=dialog.FolderName;};
        _cancel.Click+=(_,_)=>_cancellation?.Cancel();_open.Click+=Open_Click;
        Closing+=(_,e)=>{if(_busy){e.Cancel=true;_status.Text="Avbryt eller vänta tills uppackningen är klar.";}else _password.Clear();};
    }
    private async void Open_Click(object sender,RoutedEventArgs e)
    {
        if(_busy)return;
        using var secured=_password.SecurePassword;var bstr=Marshal.SecureStringToBSTR(secured);var password=new char[secured.Length];
        try{Marshal.Copy(bstr,password,0,password.Length);}finally{Marshal.ZeroFreeBSTR(bstr);}
        _busy=true;_open.IsEnabled=false;_cancel.IsEnabled=true;_password.IsEnabled=false;_cancellation=new CancellationTokenSource();
        var active=true;
        try
        {
            _status.Text="Kontrollerar läsare, lösenord och krypterat innehåll…";
            using var work=new PrivateWorkspace();var runtime=await App.ExtractEngine(work.Path);
            var progress=new Progress<FileCryptoProgress>(p=>{if(active)_status.Text=p.Stage;});
            var destination=await new ArchivePackage(runtime).ExtractAsync(_package.Text,_folder.Text,password,_cancellation.Token,progress);
            active=false;_status.Text="Verifierade filer är uppackade i:\n"+destination;_password.Clear();
        }
        catch(OperationCanceledException){active=false;_status.Text="Uppackningen avbröts. Ingen resultatmapp publicerades.";}
        catch(Exception error) when(error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        {active=false;_status.Text=error is IOException or ArgumentException or InvalidOperationException?error.Message:"Arkivet kunde inte öppnas. Kontrollera filen och behörigheterna.";}
        finally{active=false;Array.Clear(password);_busy=false;_open.IsEnabled=true;_cancel.IsEnabled=false;_password.IsEnabled=true;_cancellation.Dispose();_cancellation=null;}
    }
}
