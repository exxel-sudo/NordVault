using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NordVault.Core;

namespace NordVault.App;

/// <summary>Reads Firefox exports: the password CSV from about:logins and bookmarks as HTML or JSON backup.</summary>
internal sealed class FirefoxImportWindow : Window
{
    private readonly TextBlock _summary=new(){TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,12),FontWeight=FontWeights.SemiBold};
    private readonly CheckBox _deleteCsv=new(){Content="Radera lösenordsfilen (CSV) efter importen – rekommenderas, den innehåller lösenorden i klartext",IsChecked=true,Margin=new Thickness(0,0,0,14)};
    private readonly CheckBox _stripSecrets=new(){IsChecked=true,Margin=new Thickness(0,0,0,14),Visibility=Visibility.Collapsed};
    private readonly Button _go=new(){Content="Importera krypterat",IsEnabled=false};
    private readonly List<string> _loginFiles=[];
    public List<ImportedLogin> Logins{get;}=[];
    public List<ImportedBookmark> Bookmarks{get;}=[];

    public FirefoxImportWindow()
    {
        Title="Importera från Firefox";Width=640;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        _go.Style=(Style)Application.Current.FindResource("Primary");
        var panel=new StackPanel{Margin=new Thickness(26)};
        panel.Children.Add(new TextBlock{Text="Importera lösenord och bokmärken",FontSize=23,FontWeight=FontWeights.SemiBold});
        panel.Children.Add(new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12),Text=
            "Exportera först från Firefox:\n\n"+
            "Lösenord: skriv about:logins i adressfältet → menyn ••• uppe till höger → Exportera lösenord… → spara CSV-filen.\n\n"+
            "Bokmärken: tryck Ctrl+Skift+O → Importera och säkerhetskopiera → Exportera bokmärken till HTML… (en JSON-säkerhetskopia fungerar också).\n\n"+
            "Välj sedan en eller flera av filerna här. Inloggningar för samma webbplats och användarnamn uppdateras, och länkar som redan finns läggs inte till igen."});
        var choose=new Button{Content="Välj exporterade filer…",HorizontalAlignment=HorizontalAlignment.Left};panel.Children.Add(choose);
        panel.Children.Add(_summary);panel.Children.Add(_stripSecrets);panel.Children.Add(_deleteCsv);
        panel.Children.Add(new TextBlock{TextWrapping=TextWrapping.Wrap,FontSize=12,Foreground=System.Windows.Media.Brushes.DimGray,Margin=new Thickness(0,0,0,14),Text=
            "Lösenordsfilen raderas direkt, inte via papperskorgen. Har du själv flyttat den till papperskorgen: töm den. På SSD och USB-minnen kan rester av filen ligga kvar trots radering och överskrivning hjälper inte. Gör därför helst exporten till en BitLocker-krypterad disk, eller till ett USB-minne som du formaterar efteråt. Efter importen visar NordVault hur du tar bort lösenorden ur Firefox."});
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        buttons.Children.Add(new Button{Content="Avbryt",IsCancel=true});buttons.Children.Add(_go);panel.Children.Add(buttons);Content=panel;
        _summary.Text="Ingen fil vald.";
        choose.Click+=(_,_)=>Choose();_go.Click+=(_,_)=>
        {
            // Tokens, reset codes and sessions in a link work like passwords: keep only the address part.
            if(_stripSecrets.IsChecked==true)for(var i=0;i<Bookmarks.Count;i++)if(FirefoxImport.HasSecret(Bookmarks[i].Url))Bookmarks[i]=FirefoxImport.WithoutSecret(Bookmarks[i]);
            DialogResult=true;
        };
    }

    private void Choose()
    {
        var picker=new OpenFileDialog{Title="Välj Firefox-export",Multiselect=true,Filter="Firefox-export (*.csv;*.html;*.htm;*.json)|*.csv;*.html;*.htm;*.json|Alla filer|*.*"};
        if(picker.ShowDialog(this)!=true)return;
        Discard();var problems=new List<string>();
        foreach(var path in picker.FileNames)
        {
            byte[]? bytes=null;
            try
            {
                bytes=FirefoxImport.ReadLimited(path);
                switch(FirefoxImport.Detect(bytes))
                {
                    case FirefoxImport.FileKind.Logins:Logins.AddRange(FirefoxImport.ParseLogins(bytes));_loginFiles.Add(path);break;
                    case FirefoxImport.FileKind.BookmarksHtml:Bookmarks.AddRange(FirefoxImport.ParseBookmarksHtml(Encoding.UTF8.GetString(bytes)));break;
                    case FirefoxImport.FileKind.BookmarksJson:Bookmarks.AddRange(FirefoxImport.ParseBookmarksJson(bytes));break;
                    default:problems.Add(Path.GetFileName(path)+": känns inte igen som Firefox-export");break;
                }
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or System.Text.RegularExpressions.RegexMatchTimeoutException or DecoderFallbackException)
            {problems.Add(Path.GetFileName(path)+": "+(error is UnauthorizedAccessException?"åtkomst nekad":error.Message));}
            finally{if(bytes is not null)CryptographicOperations.ZeroMemory(bytes);}
        }
        _summary.Text=$"Hittade {Logins.Count} inloggningar och {Bookmarks.Count} bokmärken."+(Logins.Count>0?" Alla inloggningar markeras Att byta, eftersom de låg i Firefox.":"")+(problems.Count>0?"\n"+string.Join("\n",problems):"");
        var secret=Bookmarks.Count(b=>FirefoxImport.HasSecret(b.Url));
        _stripSecrets.Content=$"⚠ {secret} bokmärken har hemliga koder i adressen (t.ex. token=, key=, password=, återställningslänkar). Ta bort koderna och spara bara adressen – rekommenderas.";
        _stripSecrets.Visibility=secret>0?Visibility.Visible:Visibility.Collapsed;
        _deleteCsv.IsEnabled=_loginFiles.Count>0;
        _go.IsEnabled=Logins.Count+Bookmarks.Count>0;
    }

    /// <summary>After a successful import. Returns a status sentence for the activity line.</summary>
    public string DeleteLoginFiles()
    {
        if(_deleteCsv.IsChecked!=true||_loginFiles.Count==0)return _loginFiles.Count>0?" Lösenordsfilen finns kvar – radera den själv.":"";
        var failed=0;
        foreach(var path in _loginFiles){try{File.Delete(path);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){failed++;}}
        return failed==0?" Lösenordsfilen är raderad.":" Lösenordsfilen kunde inte raderas – radera den själv.";
    }

    public void Discard()
    {
        foreach(var login in Logins)Array.Clear(login.Password);
        Logins.Clear();Bookmarks.Clear();_loginFiles.Clear();
    }
}

/// <summary>After importing logins: the copies left in Firefox are unencrypted and must go.</summary>
internal sealed class FirefoxCleanupWindow : Window
{
    public FirefoxCleanupWindow()
    {
        Title="Ta bort lösenorden ur Firefox";Width=640;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new Thickness(26)};
        panel.Children.Add(new TextBlock{Text="Importen är klar – rensa nu Firefox",FontSize=23,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,16),Text=
            "Lösenorden finns kvar i Firefox. Utan ett huvudlösenord i Firefox kan ett skadeprogram läsa dem där. Gör så här:\n\n"+
            "1. Kontrollera i NordVault att inloggningarna finns med.\n\n"+
            "2. Firefox: skriv about:logins i adressfältet → menyn ••• uppe till höger → Ta bort alla inloggningar → bekräfta.\n\n"+
            "3. Firefox: Inställningar → Integritet och säkerhet → Lösenord: avmarkera Fråga om att spara inloggningar och lösenord för webbplatser och Fyll i inloggningar och lösenord automatiskt.\n\n"+
            "4. Använder du Firefox-konto (Sync)? Borttagningen synkas till dina andra enheter. Kontrollera dem, eller stäng av synkning av lösenord.\n\n"+
            "5. Byt lösenorden i listan Att byta, viktigast först: e-post, Microsoft/Google/Apple, bank, domäner. Slå på tvåstegsverifiering med en app i telefonen – den stoppar den som bara har lösenordet."});
        var close=new Button{Content="Jag förstår",HorizontalAlignment=HorizontalAlignment.Right,IsCancel=true,IsDefault=true,Style=(Style)Application.Current.FindResource("Primary")};
        close.Click+=(_,_)=>Close();panel.Children.Add(close);Content=panel;
    }
}
