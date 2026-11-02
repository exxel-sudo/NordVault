using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NordVault.Core;

namespace NordVault.App;

public sealed class RecoveryWindow : Window
{
    private readonly BackendClient _backend;
    private readonly VolumeInfo _volume;
    private readonly PasswordBox _password = new();
    private readonly PasswordBox _repeat = new();
    private readonly Button _generate = new() { Content = "Generera starkt lösenord" };
    private readonly CheckBox _showPassword = new() { Content = "Visa lösenord", Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox _visiblePassword = new() { IsReadOnly = true, Visibility = Visibility.Collapsed };
    private readonly TextBox _target = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 15) };
    private readonly TextBox _recovery = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly CheckBox _offline = new() { Content = "Jag har sparat nyckeln säkert på en annan fysisk enhet.", IsEnabled = false, Margin = new Thickness(0, 7, 0, 16) };
    private readonly Button _prepare = new() { Content = "1. Skapa återställningsnyckel" };
    private readonly Button _save = new() { Content = "2. Spara och kontrollera nyckeln", IsEnabled = false };
    private readonly Button _start = new() { Content = "3. Starta helvolymskryptering", IsEnabled = false };
    private RecoveryMaterial? _material;
    private bool _backupVerified;
    private bool _busy;

    public RecoveryWindow(BackendClient backend, VolumeInfo volume)
    {
        _backend = backend; _volume = volume;
        Title = "Kryptera " + volume.DisplayName;
        Width = 610; Height = 720; MinHeight = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "Kryptera " + volume.DisplayName, FontSize = 25, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Hela volymen · XTS-AES-256\nSäkerhetskopiera dina filer före start. Återställningsnyckeln måste sparas på en annan fysisk enhet.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 20) });
        panel.Children.Add(new TextBlock { Text = volume.IsSystem ? "Start-PIN: 6–20 siffror (TPM + PIN)" : "Lösenord: minst 14 tecken" }); panel.Children.Add(_password);
        panel.Children.Add(new TextBlock { Text = volume.IsSystem ? "Upprepa PIN" : "Upprepa lösenord" }); panel.Children.Add(_repeat);
        if (!volume.IsSystem)
        {
            var tools = new WrapPanel(); tools.Children.Add(_generate); tools.Children.Add(_showPassword);
            panel.Children.Add(tools); panel.Children.Add(_visiblePassword);
            panel.Children.Add(new TextBlock { Text = "Generatorn skapar 24 slumpade tecken. Spara lösenordet i din lösenordshanterare.", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 12) });
        }
        panel.Children.Add(new TextBlock { Text = $"Skriv {volume.MountPoint} för att bekräfta enheten" }); panel.Children.Add(_target);
        panel.Children.Add(_prepare); panel.Children.Add(_recovery); panel.Children.Add(_save); panel.Children.Add(_offline); panel.Children.Add(_start); panel.Children.Add(_status);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _prepare.Click += Prepare_Click; _save.Click += Save_Click; _start.Click += Start_Click;
        _password.PasswordChanged += (_, _) => UpdateButtons(); _repeat.PasswordChanged += (_, _) => UpdateButtons(); _target.TextChanged += (_, _) => UpdateButtons();
        _offline.Checked += (_, _) => UpdateButtons(); _offline.Unchecked += (_, _) => UpdateButtons();
        _showPassword.Checked += (_, _) => UpdateButtons(); _showPassword.Unchecked += (_, _) => UpdateButtons();
        _generate.Click += (_, _) =>
        {
            if (_busy) return;
            var password = PasswordGenerator.Generate();
            try { var text = new string(password); _password.Password = text; _repeat.Password = text; }
            finally { Array.Clear(password); }
        };
        Closing += OnClosing;
        UpdateButtons();
    }

    private bool CredentialsValid()
    {
        if (_target.Text != _volume.MountPoint) return false;
        if (!_volume.IsSystem) return SafetyPolicy.ValidatePassword(_password.Password, _repeat.Password).Allowed;
        var pin = _password.Password;
        return pin.Length is >= 6 and <= 20 && pin.All(c => c is >= '0' and <= '9') && pin == _repeat.Password;
    }

    private void UpdateButtons()
    {
        _generate.IsEnabled = !_busy; _password.IsEnabled = !_busy; _repeat.IsEnabled = !_busy; _target.IsEnabled = !_busy;
        _visiblePassword.Visibility = _showPassword.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _visiblePassword.Text = _showPassword.IsChecked == true ? _password.Password : "";
        _prepare.IsEnabled = !_busy && _material is null && CredentialsValid();
        _save.IsEnabled = !_busy && _material is not null;
        _start.IsEnabled = !_busy && _material is not null && _backupVerified && _offline.IsChecked == true && CredentialsValid();
    }

    private BackendRequest Request(string action) => new() { Action = action, MountPoint = _volume.MountPoint, VolumeId = _volume.VolumeId, Confirmed = true };

    private async Task RunAsync(Func<Task> operation)
    {
        _busy = true; UpdateButtons();
        try { await operation(); }
        catch (Exception ex) { _status.Text = ex is InvalidOperationException or IOException or ArgumentException ? ex.Message : "Windows kunde inte bekräfta åtgärden. Kontrollera diskstatus innan nästa försök."; }
        finally { _busy = false; UpdateButtons(); }
    }

    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        if (!_prepare.IsEnabled) return;
        await RunAsync(async () =>
        {
            _status.Text = "Förbereder återställningsnyckel. Kryptering har inte startats.";
            _material = await _backend.ExecuteAsync<RecoveryMaterial>(Request("PrepareRecovery"));
            if (_material.VolumeId != _volume.VolumeId || _material.MountPoint != _volume.MountPoint) { _material = null; throw new InvalidOperationException("Volymens identitet ändrades. Välj enheten igen."); }
            _recovery.Text = _material.RecoveryPassword; _recovery.Visibility = Visibility.Visible;
            _status.Text = "Nyckeln finns nu i Windows. Spara den på en annan fysisk enhet. Avbryter du här startas ingen kryptering.";
        });
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!_save.IsEnabled || _material is null) return;
        var picker = new SaveFileDialog { Title = "Spara på en annan fysisk enhet", FileName = $"NordVault-{_volume.MountPoint[0]}-återställning-{DateTime.Now:yyyyMMdd-HHmmss}.txt", Filter = "Nyckelfil (*.txt)|*.txt", AddExtension = true, OverwritePrompt = true };
        if (picker.ShowDialog(this) != true) return;
        await RunAsync(async () =>
        {
            _backupVerified = false; _offline.IsChecked = false; _offline.IsEnabled = false;
            _ = await _backend.ExecuteAsync<OperationResult>(Request("ValidateBackup") with { FilePath = picker.FileName });
            RecoveryStore.SaveVerified(picker.FileName, _material);
            _backupVerified = true; _offline.IsEnabled = true;
            _status.Text = "Nyckelfilen har sparats och lästs tillbaka. Förvara den säkert och bekräfta för att aktivera startknappen.";
        });
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!_start.IsEnabled || _material is null) return;
        var confirm = new ConfirmationWindow("Starta kryptering", $"Kryptera hela {_volume.DisplayName}.\n\n" + (_volume.IsSystem ? "Windows gör ett starttest. Starta sedan om själv när Windows begär det. Ingen automatisk omstart görs." : "Behåll USB-enheten ansluten under krypteringen. Status visas i huvudfönstret.") + $"\n\nSkriv {_volume.MountPoint} för att starta.", _volume.MountPoint) { Owner = this };
        if (confirm.ShowDialog() != true) return;
        var success = false;
        await RunAsync(async () =>
        {
            var result = await _backend.ExecuteAsync<OperationResult>(Request("EnableEncryption") with { ProtectorId = _material.ProtectorId, BackupVerified = true, Password = _volume.IsSystem ? "" : _password.Password, Pin = _volume.IsSystem ? _password.Password : "" });
            _password.Clear(); _repeat.Clear(); _recovery.Clear(); _material = null;
            _status.Text = result.Message; success = true;
            MessageBox.Show(this, result.Message, "Kryptering", MessageBoxButton.OK, MessageBoxImage.Information);
        });
        if (success) DialogResult = true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; _status.Text = "Vänta tills Windows har svarat innan du stänger guiden."; return; }
        _password.Clear(); _repeat.Clear(); _visiblePassword.Clear(); _showPassword.IsChecked = false; _recovery.Clear(); _material = null;
    }
}
