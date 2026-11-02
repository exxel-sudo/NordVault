using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NordVault.Core;

namespace NordVault.App;

public sealed class FileCryptoPanel : UserControl
{
    private readonly bool _demo;
    private readonly GnuPgRuntime _runtime = new(Path.Combine(AppContext.BaseDirectory, "tools", "gnupg"));
    private readonly RadioButton _encrypt = new() { Content = "Kryptera fil", IsChecked = true, GroupName = "FileMode", Margin = new Thickness(0, 0, 28, 0) };
    private readonly RadioButton _decrypt = new() { Content = "Dekryptera fil", GroupName = "FileMode" };
    private readonly TextBox _input = new() { IsReadOnly = true };
    private readonly PasswordBox _password = new();
    private readonly PasswordBox _repeat = new();
    private readonly TextBlock _repeatLabel = new() { Text = "Upprepa lösenordet" };
    private readonly Grid _passwordFields = new();
    private readonly CheckBox _show = new() { Content = "Visa lösenord", Margin = new Thickness(0, 3, 0, 7) };
    private readonly TextBox _visible = new() { IsReadOnly = true, Visibility = Visibility.Collapsed };
    private readonly Button _choose = new() { Content = "Välj fil…" };
    private readonly Button _generate = new() { Content = "Generera starkt lösenord" };
    private readonly Button _start = new() { Content = "Kryptera och spara…", IsEnabled = false };
    private readonly Button _cancel = new() { Content = "Avbryt filåtgärd", IsEnabled = false };
    private readonly Button _archive=new(){Content="Skapa självuppackande arkiv…"};
    private readonly TextBlock _status = new() { Text = "Välj en fil. Originalet behålls och befintliga resultat skrivs aldrig över.", TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Height = 7, Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _engine = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)), FontSize = 12 };
    private CancellationTokenSource? _cancellation;
    private bool _blocked;
    public bool IsBusy { get; private set; }
    internal bool CanChooseFile => _choose.IsEnabled;
    public event Action? BusyChanged;
    public event Action<string>? ActivityChanged;
    public void SetBlocked(bool blocked) { if (_blocked == blocked) return; _blocked = blocked; Update(); }
    public void ClearSecrets(){_password.Clear();_repeat.Clear();_visible.Clear();_show.IsChecked=false;}

    public FileCryptoPanel(bool demo)
    {
        _demo = demo;
        var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };
        panel.Children.Add(new TextBlock { Text = "Skydda enskilda filer", FontSize = 25, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Lösenordskryptering med kontroll av filens integritet. Fungerar utan administratör och utan BitLocker.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)), Margin = new Thickness(0, 9, 0, 14) });
        var mode = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        mode.Children.Add(_encrypt); mode.Children.Add(_decrypt); panel.Children.Add(mode);
        panel.Children.Add(new TextBlock { Text = "Fil att behandla" });
        var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(_input); Grid.SetColumn(_choose, 1); _choose.Margin = new Thickness(12, 5, 0, 12); row.Children.Add(_choose); panel.Children.Add(row);
        _passwordFields.ColumnDefinitions.Add(new ColumnDefinition()); _passwordFields.ColumnDefinitions.Add(new ColumnDefinition());
        _passwordFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); _passwordFields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _passwordFields.Children.Add(new TextBlock { Text = "Lösenord · minst 16 tecken för nya filer" });
        Grid.SetRow(_password, 1); _password.Margin = new Thickness(0, 5, 12, 12); _passwordFields.Children.Add(_password);
        Grid.SetColumn(_repeatLabel, 1); _passwordFields.Children.Add(_repeatLabel);
        Grid.SetRow(_repeat, 1); Grid.SetColumn(_repeat, 1); _passwordFields.Children.Add(_repeat);
        panel.Children.Add(_passwordFields);
        var passwordTools = new StackPanel { Orientation = Orientation.Horizontal }; passwordTools.Children.Add(_generate); passwordTools.Children.Add(_show); panel.Children.Add(passwordTools); panel.Children.Add(_visible);
        panel.Children.Add(new TextBlock { Text = "Spara lösenordet i din lösenordshanterare. Filkrypteringen har ingen återställningsnyckel om lösenordet försvinner.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)), FontSize = 12, Margin = new Thickness(0, 4, 0, 10) });
        var buttons = new WrapPanel(); _start.Style = (Style)Application.Current.FindResource("Primary"); buttons.Children.Add(_start); buttons.Children.Add(_cancel); panel.Children.Add(buttons);
        panel.Children.Add(_archive);
        var result = new StackPanel(); result.Children.Add(_status); result.Children.Add(_progress);
        panel.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(228, 240, 236)), CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Child = result, Margin = new Thickness(0, 8, 0, 14) });
        panel.Children.Add(_engine);
        Content = new ScrollViewer { Content = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(9), Child = panel }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 16, 0, 0) };
        _choose.Click += Choose_Click; _generate.Click += Generate_Click; _start.Click += Start_Click; _cancel.Click += (_, _) => { _cancellation?.Cancel(); _cancel.IsEnabled = false; SetStatus("Avbryter och tar bort den ofärdiga resultatfilen…"); };
        _archive.Click+=Archive_Click;
        _password.PasswordChanged += (_, _) => Update(); _repeat.PasswordChanged += (_, _) => Update(); _show.Checked += (_, _) => Update(); _show.Unchecked += (_, _) => Update();
        _encrypt.Checked += (_, _) => Update(); _decrypt.Checked += (_, _) => Update();
        if (demo) { _input.Text = @"C:\Privat\resehandlingar.pdf"; _status.Text = "DEMO · filåtgärder är avstängda i förhandsvisningen."; }
        Update();
    }

    private void Update()
    {
        var encrypt = _encrypt.IsChecked == true;
        var ready = !_demo && !IsBusy && !_blocked;
        _choose.IsEnabled = ready; _generate.IsEnabled = ready && encrypt;
        _encrypt.IsEnabled = ready; _decrypt.IsEnabled = ready; _password.IsEnabled = ready; _repeat.IsEnabled = ready;
        _repeat.Visibility = _repeatLabel.Visibility = encrypt ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumnSpan(_password, encrypt ? 1 : 2);
        _start.Content = encrypt ? "Kryptera och spara…" : "Verifiera, dekryptera och spara…";
        var valid = false;
        var secret = GetPassword(_password);
        var confirmation = GetPassword(_repeat);
        try { FileCryptoService.ValidatePassword(secret, !encrypt); valid = !encrypt || secret.AsSpan().SequenceEqual(confirmation); }
        catch (ArgumentException) { }
        finally { Array.Clear(secret); Array.Clear(confirmation); }
        _start.IsEnabled = ready && valid && File.Exists(_input.Text) && _runtime.IsInstalled;
        _archive.IsEnabled=ready&&encrypt&&valid&&_runtime.IsInstalled&&File.Exists(Path.Combine(AppContext.BaseDirectory,"NordVault.Reader.exe"))&&ArchiveTemplateHash() is not null;
        _cancel.IsEnabled = IsBusy && _cancellation?.IsCancellationRequested == false;
        _visible.Visibility = _show.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _visible.Text = _show.IsChecked == true ? _password.Password : "";
        _engine.Text = _runtime.IsInstalled || _demo ? "AES-256 · autentiserad OCB · GnuPG 2.5.24 · upp till 64 GiB per fil\nMotorns kontrollsummor verifieras före varje filåtgärd." : "GnuPG-filmotorn saknas. Kör scripts/Install-GnuPG.ps1 och bygg NordVault igen.";
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = _encrypt.IsChecked == true ? "Välj fil att kryptera" : "Välj krypterad fil", CheckFileExists = true, Multiselect = false, Filter = _encrypt.IsChecked == true ? "Alla filer|*.*" : "Krypterade filer (*.gpg;*.pgp)|*.gpg;*.pgp|Alla filer|*.*" };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        _input.Text = picker.FileName; Update();
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var password = PasswordGenerator.Generate();
        try { var text = new string(password); _password.Password = text; _repeat.Password = text; SetStatus("Ett slumpat lösenord med 24 tecken har skapats. Spara det innan du krypterar."); }
        finally { Array.Clear(password); }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!_start.IsEnabled || IsBusy || _blocked) return;
        var encrypt = _encrypt.IsChecked == true;
        var input = _input.Text;
        var baseName = Path.GetFileNameWithoutExtension(input);
        var outputName = encrypt ? Path.GetFileName(input) + ".gpg" : Path.GetFileNameWithoutExtension(baseName) + "-dekrypterad" + Path.GetExtension(baseName);
        var save = new SaveFileDialog { Title = encrypt ? "Spara krypterad kopia" : "Spara dekrypterad kopia", FileName = outputName, Filter = encrypt ? "Krypterad fil (*.gpg)|*.gpg" : "Alla filer|*.*", AddExtension = encrypt, OverwritePrompt = true };
        if (save.ShowDialog(Window.GetWindow(this)) != true) return;
        var secret = GetPassword(_password);
        IsBusy = true; _cancellation = new CancellationTokenSource(); _progress.Visibility = Visibility.Visible; _progress.IsIndeterminate = true; Update(); BusyChanged?.Invoke();
        var acceptProgress = true;
        try
        {
            var progress = new Progress<FileCryptoProgress>(p =>
            {
                if (!acceptProgress) return;
                _progress.IsIndeterminate = p.Percentage is null;
                if (p.Percentage is { } percent) _progress.Value = percent;
                SetStatus(p.Stage + (p.Bytes > 0 ? $" · {p.Bytes / 1048576.0:0.0} MiB" : ""));
            });
            var service = new FileCryptoService(_runtime);
            var result = encrypt ? await service.EncryptAsync(input, save.FileName, secret, _cancellation.Token, progress) : await service.DecryptAsync(input, save.FileName, secret, _cancellation.Token, progress);
            acceptProgress = false;
            SetStatus((encrypt ? "Krypterad kopia sparad: " : "Verifierad och dekrypterad kopia sparad: ") + result.OutputPath + "\nOriginalfilen finns kvar." + (result.CleanupWarning is null ? "" : "\n" + result.CleanupWarning));
            _password.Clear(); _repeat.Clear(); _show.IsChecked = false;
        }
        catch (OperationCanceledException) { acceptProgress = false; SetStatus("Filåtgärden avbröts. Originalet finns kvar och ingen resultatfil publicerades."); }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { acceptProgress = false; SetStatus(ex is IOException or ArgumentException or InvalidOperationException ? ex.Message : "Windows nekade filåtgärden. Kontrollera filens behörigheter och välj en lokal resultatmapp."); }
        finally { acceptProgress = false; Array.Clear(secret); IsBusy = false; _cancellation.Dispose(); _cancellation = null; _progress.Visibility = Visibility.Collapsed; Update(); BusyChanged?.Invoke(); }
    }

    private void SetStatus(string status) { _status.Text = status; ActivityChanged?.Invoke(status); }

    internal static string? ArchiveTemplateHash()
    {
        using var resource=typeof(FileCryptoPanel).Assembly.GetManifestResourceStream("NordVault.ArchiveTemplate.sha256");
        if(resource is null)return null;using var reader=new StreamReader(resource);return reader.ReadToEnd().Trim();
    }
    private async void Archive_Click(object sender,RoutedEventArgs e)
    {
        if(!_archive.IsEnabled||IsBusy||_blocked)return;
        var files=new OpenFileDialog{Title="Välj filer till självuppackande lösenordsarkiv",Multiselect=true,Filter="Alla filer|*.*"};if(files.ShowDialog(Window.GetWindow(this))!=true)return;
        var save=new SaveFileDialog{Title="Spara självuppackande Windows x64-arkiv",FileName="NordVault-privat-arkiv.exe",Filter="Självuppackande arkiv (*.exe)|*.exe",AddExtension=true,OverwritePrompt=true};if(save.ShowDialog(Window.GetWindow(this))!=true)return;
        var password=GetPassword(_password);IsBusy=true;_cancellation=new CancellationTokenSource();_progress.Visibility=Visibility.Visible;_progress.IsIndeterminate=true;Update();BusyChanged?.Invoke();var active=true;
        try
        {
            var progress=new Progress<FileCryptoProgress>(p=>{if(active)SetStatus(p.Stage);});
            await new ArchivePackage(_runtime).CreateAsync(files.FileNames,save.FileName,password,Path.Combine(AppContext.BaseDirectory,"NordVault.Reader.exe"),ArchiveTemplateHash()!,_cancellation.Token,progress);
            active=false;SetStatus("Självuppackande arkiv sparat: "+save.FileName+"\nÖppnas på Windows x64 utan NordVault eller .NET installerat. Originalen finns kvar.");ClearSecrets();
        }
        catch(OperationCanceledException){active=false;SetStatus("Arkivskapandet avbröts. Originalfilerna finns kvar.");}
        catch(Exception error)when(error is IOException or ArgumentException or InvalidOperationException or UnauthorizedAccessException){active=false;SetStatus(error is UnauthorizedAccessException?"Windows nekade åtkomst till filerna eller resultatmappen.":error.Message);}
        finally{active=false;Array.Clear(password);IsBusy=false;_cancellation.Dispose();_cancellation=null;_progress.Visibility=Visibility.Collapsed;Update();BusyChanged?.Invoke();}
    }

    private static char[] GetPassword(PasswordBox box)
    {
        using var secured = box.SecurePassword;
        var bstr = Marshal.SecureStringToBSTR(secured);
        try { var chars = new char[secured.Length]; Marshal.Copy(bstr, chars, 0, chars.Length); return chars; }
        finally { Marshal.ZeroFreeBSTR(bstr); }
    }
}
