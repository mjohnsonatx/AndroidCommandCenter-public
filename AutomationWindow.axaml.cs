using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AndroidCommandCenter.Models;
using AndroidCommandCenter.Services;
using AndroidCommandCenter.UI;

namespace AndroidCommandCenter;

public partial class AutomationWindow : Window
{
    private readonly GitHubService _github = new();
    private readonly FieldValidator _v;
    private readonly bool _ready;
    private readonly bool _hasStoredSmtpPassword;
    private CancellationTokenSource? _branchCts;

    public AutomationWindow(AutomationDraft draft, bool hasStoredSmtpPassword)
    {
        InitializeComponent();
        _hasStoredSmtpPassword = hasStoredSmtpPassword;

        RemoteBox.Text = draft.Remote;
        BranchBox.Text = draft.Branch;
        GitHubOwnerBox.Text = draft.Owner;
        GitHubRepoBox.Text = draft.Repository;
        GitHubTokenBox.Text = draft.Token;
        RecurringCheck.IsChecked = draft.RecurringEnabled;
        RunTimesBox.Text = draft.RunTimes;
        FailureCombo.SelectedIndex = (int)draft.FailureAction;

        EmailCheck.IsChecked = draft.Email.Send;
        ToBox.Text = draft.Email.To;
        FromBox.Text = draft.Email.From;
        SmtpHostBox.Text = draft.Email.Host;
        SmtpPortBox.Text = draft.Email.Port.ToString();
        SslCheck.IsChecked = draft.Email.Ssl;
        UserBox.Text = draft.Email.UserName;
        PasswordBox.Text = draft.Email.Password;
        if (hasStoredSmtpPassword && draft.Email.Password.Length == 0)
            PasswordBox.Watermark = "(unchanged — stored in keyring)";

        bool EmailOn() => EmailCheck.IsChecked == true;
        bool SchedOn() => RecurringCheck.IsChecked == true;

        _v = new FieldValidator(SaveBtn, SummaryText)
            .Rule(RemoteBox, RemoteErr, t => FieldValidator.GitName(t, "Remote"))
            .Rule(BranchBox, BranchErr, t => FieldValidator.GitName(t, "Branch"))
            .Rule(RunTimesBox, RunTimesErr, ValidateTimes, SchedOn)
            .Rule(ToBox, ToErr, t => FieldValidator.Email(t, "Recipient"), EmailOn)
            .Rule(FromBox, FromErr, t => FieldValidator.Email(t, "From address"), EmailOn)
            .Rule(SmtpHostBox, HostErr, t => FieldValidator.Required(t, "SMTP host"), EmailOn)
            .Rule(SmtpPortBox, PortErr, FieldValidator.Port, EmailOn)
            .Rule(PasswordBox, PasswordErr, t =>
                // Only required when a username is given and nothing is stored.
                UserBox.Text?.Trim().Length > 0 && t.Length == 0 && !_hasStoredSmtpPassword
                    ? "SMTP password is required when a username is set." : null, EmailOn);

        _ready = true;
        UpdateScheduleVisibility();
        UpdateEmailVisibility();
        Revalidate();
    }

    private void Field_Changed(object? s, TextChangedEventArgs e) => Revalidate();
    private void Field_Checked(object? s, RoutedEventArgs e) => Revalidate();

    private void Revalidate()
    {
        if (!_ready) return;
        bool ok = _v.Run();
        SaveRunBtn.IsEnabled = ok;
        UpdateNextRunInfo();
    }

    private void Failure_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        // Silent failure on an unattended schedule can ship a broken build.
        bool silent = FailureCombo.SelectedIndex == (int)TestFailureAction.SilentFailure;
        bool confirm = FailureCombo.SelectedIndex == (int)TestFailureAction.WarningWithConfirm;
        bool sched = RecurringCheck.IsChecked == true;

        FailureWarn.IsVisible = silent || (confirm && sched);
        FailureWarn.Text =
            silent ? "Silent mode deploys even when tests fail. Not recommended for a Play Store app."
            : confirm && sched ? "This mode waits for a human. A scheduled run will block until you answer."
            : "";
    }

    private void RecurringCheck_Changed(object? s, RoutedEventArgs e)
    {
        UpdateScheduleVisibility();
        Failure_Changed(s, null!);
        Revalidate();
    }

    private void EmailCheck_Changed(object? s, RoutedEventArgs e)
    {
        UpdateEmailVisibility();
        Revalidate();
    }

    private void UpdateScheduleVisibility() => SchedulePanel.IsVisible = RecurringCheck.IsChecked == true;
    private void UpdateEmailVisibility() => EmailPanel.IsVisible = EmailCheck.IsChecked == true;

    private void UpdateNextRunInfo()
    {
        if (RecurringCheck.IsChecked != true || !TryParseTimes(RunTimesBox.Text ?? "", out var times))
        { NextRunInfo.Text = ""; return; }

        var now = DateTime.Now;
        var next = times
            .Select(t => now.Date.Add(t))
            .Select(d => d <= now ? d.AddDays(1) : d)
            .Min();
        NextRunInfo.Text = $"Next run: {next:ddd HH:mm} (in {(next - now).TotalHours:F1} h) · " +
                           $"{times.Count} time(s)/day";
    }

    private static string? ValidateTimes(string v) =>
        TryParseTimes(v, out _) ? null : "Use 24-hour HH:mm values separated by commas (e.g. 02:00, 14:30).";

    private static bool TryParseTimes(string value, out List<TimeSpan> times)
    {
        times = [];
        var seen = new HashSet<TimeSpan>();
        foreach (var raw in value.Split([',', ';'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TimeSpan.TryParseExact(raw, [@"h\:mm", @"hh\:mm"],
                    CultureInfo.InvariantCulture, out var t) ||
                t < TimeSpan.Zero || t >= TimeSpan.FromDays(1))
                return false;
            if (seen.Add(t)) times.Add(t);
        }
        times.Sort();
        return times.Count > 0;
    }

    private async void LoadBranches_Click(object? s, RoutedEventArgs e)
    {
        string owner = GitHubOwnerBox.Text?.Trim() ?? "";
        string repo = GitHubRepoBox.Text?.Trim() ?? "";
        if (owner.Length == 0 || repo.Length == 0)
        {
            BranchStatusText.Text = "Enter owner and repository first.";
            return;
        }
        _branchCts?.Cancel();
        _branchCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        LoadBranchesBtn.IsEnabled = false;
        BranchStatusText.Text = "Loading branches…";
        try
        {
            var branches = await _github.GetBranchesAsync(owner, repo, GitHubTokenBox.Text ?? "", _branchCts.Token);
            BranchCombo.ItemsSource = branches;
            string want = BranchBox.Text?.Trim() ?? "";
            BranchCombo.SelectedItem =
                branches.FirstOrDefault(b => string.Equals(b, want, StringComparison.OrdinalIgnoreCase))
                ?? branches.FirstOrDefault();
            if (BranchCombo.SelectedItem is string b) BranchBox.Text = b;
            BranchStatusText.Text = branches.Count == 0
                ? "No branches found." : $"✔ Loaded {branches.Count} branch(es).";
        }
        catch (Exception ex) { BranchStatusText.Text = Toaster.Friendly(ex); }
        finally { LoadBranchesBtn.IsEnabled = true; Revalidate(); }
    }

    private void BranchCombo_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (BranchCombo.SelectedItem is string b) BranchBox.Text = b;
    }

    private AutomationDraft? Build()
    {
        if (!_v.Run()) return null;
        int port = int.TryParse(SmtpPortBox.Text, out var p) ? p : 587;
        return new AutomationDraft(
            RemoteBox.Text!.Trim(), BranchBox.Text!.Trim(),
            GitHubOwnerBox.Text?.Trim() ?? "", GitHubRepoBox.Text?.Trim() ?? "",
            GitHubTokenBox.Text ?? "",
            RecurringCheck.IsChecked == true, RunTimesBox.Text?.Trim() ?? "",
            (TestFailureAction)Math.Max(0, FailureCombo.SelectedIndex),
            new EmailDraft(
                EmailCheck.IsChecked == true, ToBox.Text?.Trim() ?? "", FromBox.Text?.Trim() ?? "",
                SmtpHostBox.Text?.Trim() ?? "", port, SslCheck.IsChecked == true,
                UserBox.Text?.Trim() ?? "", PasswordBox.Text ?? ""));
    }

    public sealed record AutomationDialogResult(AutomationDraft Draft, bool RunNow);

    private void Save_Click(object? s, RoutedEventArgs e)
    {
        if (Build() is { } d) Close(new AutomationDialogResult(d, false));
    }

    private void SaveRun_Click(object? s, RoutedEventArgs e)
    {
        if (Build() is { } d) Close(new AutomationDialogResult(d, true));
    }

    private void Cancel_Click(object? s, RoutedEventArgs e) => Close(null);

    protected override void OnClosed(EventArgs e) { _branchCts?.Cancel(); base.OnClosed(e); }
}