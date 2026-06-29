using System.Windows;
using System.Windows.Controls;
using AndroidCommandCenter.Models;
using System.Globalization;
using AndroidCommandCenter.Services;

namespace AndroidCommandCenter;

public partial class AutomationWindow : Window
{
    private readonly AutomationConfig _cfg;
    private readonly CiCdConfig _ciCd;
    private readonly GitHubService _github = new();

    public bool RunNow { get; private set; }

    public AutomationWindow(AutomationConfig cfg, CiCdConfig ciCd)
    {
        InitializeComponent();
        _cfg = cfg;
        _ciCd = ciCd;

        RemoteBox.Text = cfg.RemoteName;
        BranchBox.Text = cfg.SourceBranch;
        GitHubOwnerBox.Text = ciCd.GitHubOwner;
        GitHubRepoBox.Text = ciCd.GitHubRepository;
        GitHubTokenBox.Password = ciCd.GitHubToken;
        RecurringCheck.IsChecked = cfg.RecurringRunsEnabled;
        RunTimesBox.Text = cfg.RecurringRunTimes;
        FailureCombo.SelectedIndex = (int)cfg.FailureAction;

        EmailCheck.IsChecked = cfg.Email.SendEmail;
        ToBox.Text = cfg.Email.ToAddress;
        FromBox.Text = cfg.Email.FromAddress;
        SmtpHostBox.Text = cfg.Email.SmtpHost;
        SmtpPortBox.Text = cfg.Email.SmtpPort.ToString();
        SslCheck.IsChecked = cfg.Email.EnableSsl;
        UserBox.Text = cfg.Email.UserName;
        PasswordBox.Password = cfg.Email.Password;

        UpdateScheduleVisibility();
        UpdateEmailVisibility();
        Validate();
    }

    private void Field_Changed(object s, TextChangedEventArgs e) => Validate();
    private void GitHubSource_Changed(object s, TextChangedEventArgs e) => Validate();
    private void GitHubToken_Changed(object s, RoutedEventArgs e) => Validate();

    private async void LoadBranches_Click(object s, RoutedEventArgs e)
    {
        string owner = GitHubOwnerBox.Text.Trim();
        string repo = GitHubRepoBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            BranchStatusText.Text = "Enter owner and repository first.";
            return;
        }

        LoadBranchesBtn.IsEnabled = false;
        BranchStatusText.Text = "Loading branches...";

        try
        {
            var branches = await _github.GetBranchesAsync(
                owner,
                repo,
                GitHubTokenBox.Password,
                CancellationToken.None);

            BranchCombo.ItemsSource = branches;
            string selectedBranch = BranchBox.Text.Trim();
            BranchCombo.SelectedItem = branches.Contains(selectedBranch, StringComparer.OrdinalIgnoreCase)
                ? branches.First(b => string.Equals(b, selectedBranch, StringComparison.OrdinalIgnoreCase))
                : branches.FirstOrDefault();

            if (BranchCombo.SelectedItem is string branch)
                BranchBox.Text = branch;

            BranchStatusText.Text = branches.Count == 0
                ? "No branches found."
                : $"Loaded {branches.Count} branch(es).";
        }
        catch (Exception ex)
        {
            BranchStatusText.Text = ex.Message;
        }
        finally
        {
            LoadBranchesBtn.IsEnabled = true;
            Validate();
        }
    }

    private void BranchCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (BranchCombo.SelectedItem is string branch)
            BranchBox.Text = branch;
    }

    private void RecurringCheck_Changed(object s, RoutedEventArgs e)
    {
        UpdateScheduleVisibility();
        Validate();
    }

    private void EmailCheck_Changed(object s, RoutedEventArgs e)
    {
        UpdateEmailVisibility();
        Validate();
    }

    private void UpdateScheduleVisibility() =>
        SchedulePanel.Visibility = RecurringCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateEmailVisibility() =>
        EmailPanel.Visibility = EmailCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void Validate()
    {
        bool git = IsSimpleGitName(RemoteBox.Text) && IsSimpleGitName(BranchBox.Text);
        bool scheduleEnabled = RecurringCheck.IsChecked == true;
        bool schedule = !scheduleEnabled || TryParseRunTimes(RunTimesBox.Text, out _);
        bool emailEnabled = EmailCheck.IsChecked == true;
        bool email = !emailEnabled ||
                     (!string.IsNullOrWhiteSpace(ToBox.Text) &&
                      !string.IsNullOrWhiteSpace(FromBox.Text) &&
                      !string.IsNullOrWhiteSpace(SmtpHostBox.Text) &&
                      int.TryParse(SmtpPortBox.Text, out int port) &&
                      port > 0);

        SaveBtn.IsEnabled = git && schedule && email;
        SaveRunBtn.IsEnabled = git && schedule && email;
    }

    private bool SaveConfig()
    {
        if (!TryParseRunTimes(RunTimesBox.Text, out var runTimes) && RecurringCheck.IsChecked == true)
            return false;

        int port = 587;
        if (!string.IsNullOrWhiteSpace(SmtpPortBox.Text) &&
            !int.TryParse(SmtpPortBox.Text, out port))
        {
            return false;
        }

        _cfg.RemoteName = RemoteBox.Text.Trim();
        _cfg.SourceBranch = BranchBox.Text.Trim();
        _ciCd.GitHubOwner = GitHubOwnerBox.Text.Trim();
        _ciCd.GitHubRepository = GitHubRepoBox.Text.Trim();
        _ciCd.GitHubToken = GitHubTokenBox.Password;
        _cfg.RecurringRunsEnabled = RecurringCheck.IsChecked == true;
        _cfg.RecurringRunTimes = runTimes.Count > 0
            ? string.Join(", ", runTimes.Select(t => t.ToString(@"hh\:mm", CultureInfo.InvariantCulture)))
            : RunTimesBox.Text.Trim();
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

    private static bool TryParseRunTimes(string value, out List<TimeSpan> times)
    {
        times = [];
        var seen = new HashSet<TimeSpan>();

        foreach (string raw in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TimeSpan.TryParseExact(
                    raw,
                    [@"h\:mm", @"hh\:mm"],
                    CultureInfo.InvariantCulture,
                    out var time) ||
                time < TimeSpan.Zero ||
                time >= TimeSpan.FromDays(1))
            {
                return false;
            }

            if (seen.Add(time))
                times.Add(time);
        }

        times.Sort();
        return times.Count > 0;
    }
}
