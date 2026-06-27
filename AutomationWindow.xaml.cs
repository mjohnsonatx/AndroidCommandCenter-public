using System.Windows;
using System.Windows.Controls;
using AndroidCommandCenter.Models;

namespace AndroidCommandCenter;

public partial class AutomationWindow : Window
{
    private readonly AutomationConfig _cfg;

    public bool RunNow { get; private set; }

    public AutomationWindow(AutomationConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;

        RemoteBox.Text = cfg.RemoteName;
        BranchBox.Text = cfg.SourceBranch;
        FailureCombo.SelectedIndex = (int)cfg.FailureAction;

        EmailCheck.IsChecked = cfg.Email.SendEmail;
        ToBox.Text = cfg.Email.ToAddress;
        FromBox.Text = cfg.Email.FromAddress;
        SmtpHostBox.Text = cfg.Email.SmtpHost;
        SmtpPortBox.Text = cfg.Email.SmtpPort.ToString();
        SslCheck.IsChecked = cfg.Email.EnableSsl;
        UserBox.Text = cfg.Email.UserName;
        PasswordBox.Password = cfg.Email.Password;

        UpdateEmailVisibility();
        Validate();
    }

    private void Field_Changed(object s, TextChangedEventArgs e) => Validate();

    private void EmailCheck_Changed(object s, RoutedEventArgs e)
    {
        UpdateEmailVisibility();
        Validate();
    }

    private void UpdateEmailVisibility() =>
        EmailPanel.Visibility = EmailCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void Validate()
    {
        bool git = IsSimpleGitName(RemoteBox.Text) && IsSimpleGitName(BranchBox.Text);
        bool emailEnabled = EmailCheck.IsChecked == true;
        bool email = !emailEnabled ||
                     (!string.IsNullOrWhiteSpace(ToBox.Text) &&
                      !string.IsNullOrWhiteSpace(FromBox.Text) &&
                      !string.IsNullOrWhiteSpace(SmtpHostBox.Text) &&
                      int.TryParse(SmtpPortBox.Text, out int port) &&
                      port > 0);

        SaveBtn.IsEnabled = git && email;
        SaveRunBtn.IsEnabled = git && email;
    }

    private bool SaveConfig()
    {
        if (!int.TryParse(SmtpPortBox.Text, out int port)) return false;

        _cfg.RemoteName = RemoteBox.Text.Trim();
        _cfg.SourceBranch = BranchBox.Text.Trim();
        _cfg.FailureAction = (TestFailureAction)Math.Max(0, FailureCombo.SelectedIndex);

        _cfg.Email.SendEmail = EmailCheck.IsChecked == true;
        _cfg.Email.ToAddress = ToBox.Text.Trim();
        _cfg.Email.FromAddress = FromBox.Text.Trim();
        _cfg.Email.SmtpHost = SmtpHostBox.Text.Trim();
        _cfg.Email.SmtpPort = port;
        _cfg.Email.EnableSsl = SslCheck.IsChecked == true;
        _cfg.Email.UserName = UserBox.Text.Trim();
        _cfg.Email.Password = PasswordBox.Password;
        return true;
    }

    private void Save_Click(object s, RoutedEventArgs e)
    {
        if (!SaveConfig()) return;
        DialogResult = true;
        Close();
    }

    private void SaveRun_Click(object s, RoutedEventArgs e)
    {
        if (!SaveConfig()) return;
        RunNow = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object s, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static bool IsSimpleGitName(string value)
    {
        value = value.Trim();
        return !string.IsNullOrWhiteSpace(value) &&
               !value.Any(char.IsWhiteSpace) &&
               !value.Contains('"') &&
               !value.Contains('\'');
    }
}
