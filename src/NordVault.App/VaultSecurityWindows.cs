using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using NordVault.Core;

namespace NordVault.App;

internal static class CaptureGuard
{
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd,uint affinity);
    /// <summary>Hides the window from screenshots and screen sharing where Windows supports it.</summary>
    public static void Apply(Window window)=>window.SourceInitialized+=(_,_)=>SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle,0x11);
}

/// <summary>Shows new recovery codes exactly once. They are never saved by NordVault.</summary>
internal sealed class RecoveryCodesWindow : Window
{
    private readonly TextBlock _codes=new(){FontFamily=new FontFamily("Consolas"),FontSize=19,Margin=new Thickness(0,8,0,14),LineHeight=30};
    private bool _confirmed;
    public RecoveryCodesWindow(IReadOnlyList<char[]> codes)
    {
        Title="Återställningskoder";Width=640;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;CaptureGuard.Apply(this);
        var panel=new StackPanel{Margin=new Thickness(26)};
        panel.Children.Add(new TextBlock{Text="Dina återställningskoder",FontSize=23,FontWeight=FontWeights.SemiBold});
        panel.Children.Add(new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,4),Text=
            "Om du glömmer huvudlösenordet kan du låsa upp valvet med en av koderna och välja ett nytt huvudlösenord. Varje kod fungerar en gång.\n\n"+
            "Skriv ut eller skriv av koderna på papper nu. Förvara papperet utanför datorn, till exempel i ett kassaskåp eller hos någon du litar på. "+
            "Spara dem inte i en fil, i mejl, i molnet eller i valvet. Koderna visas bara den här gången."});
        _codes.Text=string.Join("\n",codes.Select((c,i)=>$"{i+1}.  {VaultKeyring.Format(c)}"));panel.Children.Add(_codes);
        var confirm=new CheckBox{Content="Jag har skrivit ut eller skrivit av koderna och förvarar dem säkert",Margin=new Thickness(0,0,0,14)};panel.Children.Add(confirm);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var print=new Button{Content="Skriv ut…"};var done=new Button{Content="Klar",IsEnabled=false,Style=(Style)Application.Current.FindResource("Primary")};
        buttons.Children.Add(print);buttons.Children.Add(done);panel.Children.Add(buttons);Content=panel;
        confirm.Checked+=(_,_)=>done.IsEnabled=true;confirm.Unchecked+=(_,_)=>done.IsEnabled=false;
        done.Click+=(_,_)=>{_confirmed=true;Close();};
        print.Click+=(_,_)=>
        {
            var dialog=new PrintDialog();if(dialog.ShowDialog()!=true)return;
            var page=new StackPanel{Margin=new Thickness(60),Width=dialog.PrintableAreaWidth};
            page.Children.Add(new TextBlock{Text="NordVault – återställningskoder",FontSize=20,FontWeight=FontWeights.SemiBold});
            page.Children.Add(new TextBlock{Text="Skapade "+DateTime.Now.ToString("yyyy-MM-dd")+". Varje kod fungerar en gång. Förvara papperet säkert, utanför datorn.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,16)});
            page.Children.Add(new TextBlock{Text=_codes.Text,FontFamily=new FontFamily("Consolas"),FontSize=16,LineHeight=28});
            page.Measure(new Size(dialog.PrintableAreaWidth,dialog.PrintableAreaHeight));page.Arrange(new Rect(page.DesiredSize));
            dialog.PrintVisual(page,"NordVault återställningskoder");
        };
        Closing+=(_,e)=>
        {
            if(!_confirmed&&MessageBox.Show(this,"Koderna visas inte igen. Stänga ändå?","Återställningskoder",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes){e.Cancel=true;return;}
            _codes.Text="";
        };
    }
}

/// <summary>New master password; the current one is asked for unless the vault was opened with a recovery code.</summary>
internal sealed class NewMasterPasswordWindow : Window
{
    private readonly PasswordBox _current=new(),_new=new(),_repeat=new();
    private readonly TextBlock _error=new(){Foreground=Brushes.Firebrick,TextWrapping=TextWrapping.Wrap};
    public NewMasterPasswordWindow(bool requireCurrent,string intro)
    {
        Title="Nytt huvudlösenord";Width=560;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;CaptureGuard.Apply(this);
        var panel=new StackPanel{Margin=new Thickness(26)};
        panel.Children.Add(new TextBlock{Text="Nytt huvudlösenord",FontSize=23,FontWeight=FontWeights.SemiBold});
        panel.Children.Add(new TextBlock{Text=intro,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12)});
        if(requireCurrent){panel.Children.Add(new TextBlock{Text="Nuvarande huvudlösenord"});panel.Children.Add(_current);}
        panel.Children.Add(new TextBlock{Text="Nytt huvudlösenord (minst 16 tecken)"});panel.Children.Add(_new);
        panel.Children.Add(new TextBlock{Text="Upprepa nytt huvudlösenord"});panel.Children.Add(_repeat);
        var generate=new Button{Content="Generera huvudlösenord",HorizontalAlignment=HorizontalAlignment.Left};panel.Children.Add(generate);
        var shown=new TextBox{IsReadOnly=true,Visibility=Visibility.Collapsed};panel.Children.Add(shown);panel.Children.Add(_error);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,10,0,0)};
        var ok=new Button{Content="Spara nytt huvudlösenord",Style=(Style)Application.Current.FindResource("Primary")};
        buttons.Children.Add(new Button{Content="Avbryt",IsCancel=true});buttons.Children.Add(ok);panel.Children.Add(buttons);Content=panel;
        generate.Click+=(_,_)=>{var p=PasswordGenerator.Generate();try{var t=new string(p);_new.Password=t;_repeat.Password=t;shown.Text=t;shown.Visibility=Visibility.Visible;}finally{Array.Clear(p);}};
        ok.Click+=(_,_)=>
        {
            var a=UiSecrets.Read(_new);var b=UiSecrets.Read(_repeat);
            try
            {
                FileCryptoService.ValidatePassword(a,false);
                if(!a.AsSpan().SequenceEqual(b))throw new ArgumentException("Lösenorden är inte lika.");
                if(requireCurrent&&_current.SecurePassword.Length==0)throw new ArgumentException("Ange nuvarande huvudlösenord.");
                DialogResult=true;
            }
            catch(ArgumentException error){_error.Text=error.Message;}
            finally{Array.Clear(a);Array.Clear(b);}
        };
        Closing+=(_,_)=>shown.Clear();
    }
    /// <summary>Caller clears both.</summary>
    public (char[] Current,char[] New) Take(){var result=(UiSecrets.Read(_current),UiSecrets.Read(_new));_current.Clear();_new.Clear();_repeat.Clear();return result;}
}

internal sealed class RecoveryCodeEntryWindow : Window
{
    private readonly TextBox _code=new(){FontFamily=new FontFamily("Consolas"),FontSize=17,CharacterCasing=CharacterCasing.Upper};
    public RecoveryCodeEntryWindow()
    {
        Title="Använd återställningskod";Width=600;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;CaptureGuard.Apply(this);
        var panel=new StackPanel{Margin=new Thickness(26)};
        panel.Children.Add(new TextBlock{Text="Glömt huvudlösenordet?",FontSize=23,FontWeight=FontWeights.SemiBold});
        panel.Children.Add(new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,12,0,12),Text="Skriv en av dina återställningskoder från papperet (8 grupper om 5 tecken; bindestreck behövs inte). Därefter väljer du ett nytt huvudlösenord, och koden kan inte användas igen."});
        panel.Children.Add(_code);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,10,0,0)};
        var ok=new Button{Content="Lås upp",IsDefault=true,Style=(Style)Application.Current.FindResource("Primary")};
        buttons.Children.Add(new Button{Content="Avbryt",IsCancel=true});buttons.Children.Add(ok);panel.Children.Add(buttons);Content=panel;
        ok.Click+=(_,_)=>DialogResult=true;Loaded+=(_,_)=>_code.Focus();Closing+=(_,_)=>{if(DialogResult!=true)_code.Clear();};
    }
    /// <summary>Caller clears the result.</summary>
    public char[] Take(){var result=_code.Text.ToCharArray();_code.Clear();return result;}
}
