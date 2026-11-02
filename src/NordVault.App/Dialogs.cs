using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NordVault.App;

public sealed class ConfirmationWindow : Window
{
    public ConfirmationWindow(string title, string message, string expected, bool showEula = false)
    {
        Title = title;
        Width = 540; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 15, 0, 12) });
        var input = new TextBox(); panel.Children.Add(input);
        var eula = new CheckBox { Content = "Jag har läst och accepterar Microsoft Sysinternals licens.", Margin = new Thickness(0, 0, 0, 16) };
        if (showEula)
        {
            var link = new Button { Content = "Läs licensen", HorizontalAlignment = HorizontalAlignment.Left };
            link.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://learn.microsoft.com/en-us/sysinternals/license-terms") { UseShellExecute = true });
            panel.Children.Add(link); panel.Children.Add(eula);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Avbryt", IsCancel = true };
        var confirm = new Button { Content = "Bekräfta", IsEnabled = false, Background = new SolidColorBrush(Color.FromRgb(20, 105, 91)), Foreground = Brushes.White };
        void Update() => confirm.IsEnabled = string.Equals(input.Text, expected, StringComparison.Ordinal) && (!showEula || eula.IsChecked == true);
        input.TextChanged += (_, _) => Update(); eula.Checked += (_, _) => Update(); eula.Unchecked += (_, _) => Update();
        confirm.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); panel.Children.Add(buttons); Content = panel;
    }
}

public sealed class SecretWindow : Window
{
    private readonly PasswordBox _input = new();
    public string Secret => _input.Password;
    /// <summary>Caller clears the result.</summary>
    public char[] TakeSecret() => UiSecrets.Read(_input);
    public void Clear() => _input.Clear();
    public bool UseHello { get; private set; }
    public SecretWindow(string title, string message, string confirmText = "Lås upp", bool offerHello = false)
    {
        Title = title; Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 15, 0, 12) });
        panel.Children.Add(_input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = "Avbryt", IsCancel = true });
        if (offerHello)
        {
            var hello = new Button { Content = "Använd Windows Hello" };
            hello.Click += (_, _) => { UseHello = true; _input.Clear(); DialogResult = true; };
            buttons.Children.Add(hello);
        }
        var ok = new Button { Content = confirmText, IsEnabled = false, IsDefault = true };
        _input.PasswordChanged += (_, _) => ok.IsEnabled = _input.SecurePassword.Length > 0;
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok); panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => _input.Focus();
    }
}
