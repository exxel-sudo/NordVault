using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NordVault.Core;

namespace NordVault.App;
internal sealed class VaultGateWindow : Window
{
    private readonly TextBox _folder=new(){IsReadOnly=true};
    private readonly PasswordBox _password=new();private readonly PasswordBox _repeat=new();
    private readonly TextBlock _repeatLabel=new(){Text="Upprepa huvudlösenord för nytt valv"};
    private readonly CheckBox _show=new(){Content="Visa lösenord",Margin=new Thickness(0,6,0,8)};
    private readonly TextBox _visible=new(){IsReadOnly=true,Visibility=Visibility.Collapsed};
    private readonly Button _go=new(){Content="Öppna valvet",Padding=new Thickness(17,10,17,10)};
    private readonly CheckBox _startup=new(){Content="Kräv valvets lösenord när NordVault startas",Margin=new Thickness(0,8,0,14)};
    private readonly Button _choose=new(){Content="Välj annan valvmapp…"};
    private readonly Button _generate=new(){Content="Generera huvudlösenord"};
    private readonly Button _hello=new(){Content="Lås upp med Windows Hello",Visibility=Visibility.Collapsed};
    private readonly Button _recover=new(){Content="Glömt huvudlösenordet? Använd återställningskod…",Visibility=Visibility.Collapsed,HorizontalAlignment=HorizontalAlignment.Left};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,16,0,0)};
    private bool _busy;private readonly bool _preview;public VaultSession? Session{get;private set;}
    public VaultGateWindow(bool preview=false)
    {
        _preview=preview;Title="NordVault · mitt krypterade valv";Width=660;Height=790;MinHeight=660;WindowStartupLocation=WindowStartupLocation.CenterScreen;FontFamily=new System.Windows.Media.FontFamily("Segoe UI");FontSize=14;Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(243,246,249));
        var panel=new StackPanel{Margin=new Thickness(28)};
        panel.Children.Add(new TextBlock{Text="Ett eget valv, när du behöver det.",FontSize=25,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(new TextBlock{Text="Valvet är valfritt och lagrar krypterade kopior av filer och nyckelfiler. Det reserverar ingen diskyta: storleken växer med innehållet. Diskar, USB och filkryptering fungerar utan valv. Ett förlorat valvlösenord kan inte återställas.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,20)});
        panel.Children.Add(new TextBlock{Text="Valvmapp"});panel.Children.Add(_folder);panel.Children.Add(_choose);
        panel.Children.Add(new TextBlock{Text="Huvudlösenord",Margin=new Thickness(0,12,0,0)});panel.Children.Add(_password);
        panel.Children.Add(_repeatLabel);panel.Children.Add(_repeat);panel.Children.Add(_generate);panel.Children.Add(_show);panel.Children.Add(_visible);
        _startup.IsChecked=!preview&&AppPaths.RequireVaultAtStartup;panel.Children.Add(_startup);
        var unlockRow=new WrapPanel();unlockRow.Children.Add(_go);unlockRow.Children.Add(_hello);panel.Children.Add(unlockRow);panel.Children.Add(_recover);panel.Children.Add(_status);Content=new ScrollViewer{Content=panel,Background=Background,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        _folder.Text=preview?@"F:\NordVault\Data\Vault":AppPaths.PreferredVault;
        _password.PasswordChanged+=(_,_)=>Update();_repeat.PasswordChanged+=(_,_)=>Update();_show.Checked+=(_,_)=>Update();_show.Unchecked+=(_,_)=>Update();
        _choose.Click+=(_,_)=>{if(_busy)return;var dialog=new OpenFolderDialog{Title="Välj ett befintligt valv eller en tom mapp för ett nytt"};if(dialog.ShowDialog(this)==true){_folder.Text=dialog.FolderName;Update();}};
        _generate.Click+=(_,_)=>{var password=PasswordGenerator.Generate();try{var text=new string(password);_password.Password=text;_repeat.Password=text;}finally{Array.Clear(password);}};
        _go.Click+=Open_Click;_hello.Click+=Hello_Click;_recover.Click+=Recover_Click;Closing+=(_,e)=>{if(_busy){e.Cancel=true;return;}_password.Clear();_repeat.Clear();_visible.Clear();};
        Update();if(preview){_go.IsEnabled=false;_choose.IsEnabled=false;_generate.IsEnabled=false;_status.Text="DEMO · ingen valvfil skapas i förhandsvisningen.";}
    }
    private bool Creating=>!File.Exists(Path.Combine(_folder.Text,"catalog.gpg"));
    private void Update()
    {
        var creating=Creating;_repeat.Visibility=_repeatLabel.Visibility=_generate.Visibility=creating?Visibility.Visible:Visibility.Collapsed;
        _go.Content=creating?"Skapa och öppna valvet":"Lås upp valvet";
        var root=_folder.Text;
        _hello.Visibility=!creating&&!_preview&&WindowsHello.IsEnabled(root)&&AppPaths.HelloAllowedSinceBoot(root)?Visibility.Visible:Visibility.Collapsed;
        _recover.Visibility=!creating&&!_preview&&Directory.Exists(Path.Combine(root,"keys"))&&Directory.EnumerateFiles(Path.Combine(root,"keys"),"recovery-*.gpg").Any()?Visibility.Visible:Visibility.Collapsed;
        _hello.IsEnabled=_recover.IsEnabled=!_busy;
        var password=UiSecrets.Read(_password);var repeated=UiSecrets.Read(_repeat);var valid=false;
        try{FileCryptoService.ValidatePassword(password,!creating);valid=!creating||password.AsSpan().SequenceEqual(repeated);}catch(ArgumentException){}finally{Array.Clear(password);Array.Clear(repeated);}
        _go.IsEnabled=!_busy&&valid;_choose.IsEnabled=_generate.IsEnabled=_password.IsEnabled=_repeat.IsEnabled=_startup.IsEnabled=!_busy;
        _visible.Visibility=_show.IsChecked==true?Visibility.Visible:Visibility.Collapsed;_visible.Text=_show.IsChecked==true?_password.Password:"";
        if(!_busy)_status.Text=creating?"Ett nytt valv skapas i en tom mapp. Minst 16 tecken; använd gärna generatorn och spara lösenordet säkert.":"Ange valvets huvudlösenord. Den krypterade innehållsförteckningen verifieras innan programmet öppnas.";
    }
    private async void Open_Click(object sender,RoutedEventArgs e)
    {
        if(!_go.IsEnabled||_busy)return;var creating=Creating;var root=_folder.Text;var password=UiSecrets.Read(_password);_busy=true;Update();_status.Text="Kontrollerar huvudlösenord och krypterad innehållsförteckning…";
        try
        {
            var runtime=new GnuPgRuntime(Path.Combine(AppContext.BaseDirectory,"tools","gnupg"));
            Session=creating?await VaultSession.CreateAsync(root,runtime,password):await VaultSession.OpenAsync(root,runtime,password);
            AppPaths.RememberMasterUnlock(root);
            if(creating&&MessageBox.Show(this,"Vill du skapa återställningskoder nu?\n\nMed en kod kan du låsa upp valvet och välja nytt huvudlösenord om du glömmer det. Koderna skrivs ut eller skrivs av på papper. Rekommenderas.","Återställningskoder",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.Yes)==MessageBoxResult.Yes)
            {
                _status.Text="Skapar återställningskoder…";
                var codes=await Session.CreateRecoveryCodesAsync(password);
                try{new RecoveryCodesWindow(codes){Owner=this}.ShowDialog();}finally{foreach(var code in codes)Array.Clear(code);}
            }
            Finish(root);
        }
        catch(Exception error)when(error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        {_busy=false;Update();_status.Text=error is IOException or ArgumentException or InvalidOperationException?error.Message:"Valvet kunde inte öppnas. Kontrollera behörigheter och valvmapp.";}
        finally{Array.Clear(password);}
    }
    private static GnuPgRuntime Runtime=>new(Path.Combine(AppContext.BaseDirectory,"tools","gnupg"));
    private void Finish(string root)
    {
            try {AppPaths.Remember(root);AppPaths.SetRequireVaultAtStartup(_startup.IsChecked==true);}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException)
            {MessageBox.Show(this,"Valvet öppnas, men valet av valvmapp eller lösenord vid programstart kunde inte sparas. Kontrollera mappens behörigheter.","Inställningen kunde inte sparas",MessageBoxButton.OK,MessageBoxImage.Warning);}
            _password.Clear();_repeat.Clear();_visible.Clear();_busy=false;DialogResult=true;
    }
    private async void Hello_Click(object sender,RoutedEventArgs e)
    {
        if(_busy)return;var root=_folder.Text;_busy=true;Update();_status.Text="Bekräfta med Windows Hello…";
        try{Session=await VaultSession.OpenWithHelloAsync(root,Runtime,new System.Windows.Interop.WindowInteropHelper(this).Handle);Finish(root);}
        catch(Exception error)when(error is IOException or InvalidOperationException or UnauthorizedAccessException or JsonException)
        {_busy=false;Update();_status.Text=error.Message+" Lås upp med huvudlösenordet i stället.";}
    }
    private async void Recover_Click(object sender,RoutedEventArgs e)
    {
        if(_busy)return;var root=_folder.Text;
        var entry=new RecoveryCodeEntryWindow{Owner=this};if(entry.ShowDialog()!=true)return;
        var code=entry.Take();_busy=true;Update();_status.Text="Kontrollerar återställningskoden…";VaultSession? session=null;
        try
        {
            session=await VaultSession.OpenWithRecoveryCodeAsync(root,Runtime,code);
            var choose=new NewMasterPasswordWindow(false,"Koden stämde. Välj nu ett nytt huvudlösenord. När det är sparat kan koden inte användas igen. Dina filer och lösenord i valvet behålls.\n\nWindows Hello stängs av och kan slås på igen."){Owner=this};
            if(choose.ShowDialog()!=true){session.Dispose();session=null;_busy=false;Update();_status.Text="Inget ändrades. Koden är fortfarande oanvänd.";return;}
            var (_,next)=choose.Take();
            try{_status.Text="Sparar nytt huvudlösenord…";await session.ChangeMasterPasswordAsync(null,next);}finally{Array.Clear(next);}
            Session=session;session=null;
            AppPaths.RememberMasterUnlock(root);
            MessageBox.Show(this,$"Nytt huvudlösenord är sparat och koden är förbrukad. {Session.RecoveryCodeCount} oanvända koder finns kvar.\n\nSkapa gärna nya koder under Mitt valv → Återställningskoder.","Valvet är upplåst",MessageBoxButton.OK,MessageBoxImage.Information);
            Finish(root);
        }
        catch(Exception error)when(error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or JsonException)
        {session?.Dispose();_busy=false;Update();_status.Text=error.Message;}
        finally{Array.Clear(code);}
    }
}
