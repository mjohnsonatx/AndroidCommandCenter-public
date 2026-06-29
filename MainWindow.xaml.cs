using AndroidCommandCenter.Models;
using AndroidCommandCenter.Services;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace AndroidCommandCenter;

public partial class MainWindow : Window
{
    // ═══════════════════════════════════════════════
    //  SERVICES
    // ═══════════════════════════════════════════════
    private readonly ProcessRunner _runner = new();
    private readonly AndroidService _android;
    private readonly AvdManager _avd;
    private readonly GitService _git;
    private readonly GitHubService _github = new();
    private readonly CacheManager _cache;
    private readonly WorkflowEngine _workflow = new();
    private readonly FlavorDetector _flavors = new();
    private readonly DashboardService _dashboard = new();
    private readonly TestReportService _testReports = new();
    private readonly EmailNotificationService _emailNotifications = new();
    private readonly FirebaseService _firebase;
    private readonly EnvFileService _env = new();
    private readonly GitHubIssueRestService _issueService = new();
    private readonly DeploymentReportService _deploymentReports = new();
    private readonly AppSettings _settings;
    private PipelineLogger? _pipelineLogger;

    // ═══════════════════════════════════════════════
    //  UI COLLECTIONS
    // ═══════════════════════════════════════════════
    private readonly ObservableCollection<string> _projects = new();
    private readonly ObservableCollection<string> _variants = new();
    private readonly ObservableCollection<string> _avds = new();
    private readonly ObservableCollection<string> _deviceSerials = new();
    private readonly ObservableCollection<string> _githubBranches = new();
    private readonly ObservableCollection<GitHubIssueModel> _githubIssues = new();
    private readonly ObservableCollection<GitHubCommentDto> _issueComments = new();

    // ═══════════════════════════════════════════════
    //  TIMERS / STATE
    // ═══════════════════════════════════════════════
    private readonly DispatcherTimer _deviceTimer;
    private readonly DispatcherTimer _automationTimer;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _deviceRefreshing;
    private static readonly SolidColorBrush IdleBrush = new(Color.FromRgb(0xa6, 0xe3, 0xa1));
    private static readonly SolidColorBrush BusyBrush = new(Color.FromRgb(0xf9, 0xe2, 0xaf));

    // ═══════════════════════════════════════════════
    //  CONSTRUCTOR
    // ═══════════════════════════════════════════════
    public MainWindow()
    {
        InitializeComponent();

        _android = new AndroidService(_runner);
        _avd = new AvdManager(_runner, _android.AdbPath, _android.EmulatorPath);
        _git = new GitService(_runner);
        _cache = new CacheManager(_runner, _android);
        _firebase = new FirebaseService(_runner);

        // wire log events
        _firebase.LogMessage += L; _runner.OutputReceived += L;
        _avd.LogMessage += L; _git.LogMessage += L;
        _github.LogMessage += L; _cache.LogMessage += L;
        _workflow.LogMessage += L; _flavors.LogMessage += L;
        _testReports.LogMessage += L; _emailNotifications.LogMessage += L;
        _env.LogMessage += L; _issueService.LogMessage += L;
        _deploymentReports.LogMessage += L;

        _dashboard.StatsUpdated += s => Dispatcher.Invoke(() => UpdateDashboard(s));
        _dashboard.Start();

        _settings = AppSettings.Load();
        UpdateCiCdButtonStates();

        // bind collections
        ProjectCombo.ItemsSource = _projects;
        VariantCombo.ItemsSource = _variants;
        AvdCombo.ItemsSource = _avds;
        DeviceCombo.ItemsSource = _deviceSerials;
        MainBranchCombo.ItemsSource = _githubBranches;
        IssuesGrid.ItemsSource = _githubIssues;
        IssueCommentsPanel.ItemsSource = _issueComments;

        foreach (var p in _settings.ProjectPaths) _projects.Add(p);
        if (_settings.LastSelectedProject != null)
            ProjectCombo.SelectedItem = _settings.LastSelectedProject;

        LoadSourceTabFromSettings();
        RefreshReportsTab();

        _deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _deviceTimer.Tick += async (_, _) => await RefreshDeviceList();
        _deviceTimer.Start();

        _automationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _automationTimer.Tick += async (_, _) => await CheckScheduledAutomation();
        _automationTimer.Start();

        Log("Android Command Center ready.");
        Log($"SDK: {_android.SdkPath}");
        _ = LoadAvds();
        _ = RefreshDeviceList();
    }

    private void L(string msg) => Dispatcher.Invoke(() => Log(msg));

    protected override void OnClosed(EventArgs e)
    {
        _deviceTimer.Stop(); _automationTimer.Stop();
        _dashboard.Dispose(); base.OnClosed(e);
    }

    // ═══════════════════════════════════════════════
    //  LOGGING + DASHBOARD
    // ═══════════════════════════════════════════════
    private void Log(string msg)
    {
        LogBox.AppendText(msg + Environment.NewLine);
        LogBox.ScrollToEnd();
        _pipelineLogger?.Line(msg);
    }
    private void SetStatus(string text)
    {
        StatusLabel.Text = text;
        StatusLabel.Foreground = text == "Idle" ? IdleBrush : BusyBrush;
    }
    private void UpdateDashboard(SystemStats s)
    {
        CpuBar.Value = s.CpuPercent; CpuLabel.Text = $"{s.CpuPercent:F0}%";
        MemBar.Value = s.MemoryPercent;
        MemLabel.Text = $"{s.MemoryUsedMb:N0}/{s.MemoryTotalMb:N0} MB";
        DaemonLabel.Text = s.JavaProcessCount.ToString();
    }

    // ═══════════════════════════════════════════════
    //  DEVICE MONITORING
    // ═══════════════════════════════════════════════
    private async Task RefreshDeviceList()
    {
        if (_deviceRefreshing) return;
        _deviceRefreshing = true;
        try
        {
            var devices = await _avd.GetDevicesSilent(CancellationToken.None);
            var online = devices.Where(d => d.State == "device").ToList();
            DeviceListLabel.Text = online.Count == 0
                ? "(none)" : string.Join("   ", online.Select(d => d.DisplayName));
            string? prev = DeviceCombo.SelectedItem as string;
            _deviceSerials.Clear(); _deviceSerials.Add("(All connected)");
            foreach (var d in online) _deviceSerials.Add(d.Serial);
            DeviceCombo.SelectedItem =
                _deviceSerials.Contains(prev ?? "") ? prev : "(All connected)";
        }
        catch { }
        finally { _deviceRefreshing = false; }
    }
    private void DeviceCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is string sel)
            _android.TargetDevice = sel == "(All connected)" ? null : sel;
    }

    // ═══════════════════════════════════════════════
    //  PROJECT MANAGEMENT
    // ═══════════════════════════════════════════════
    private void AddProject_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        { Title = "Select Android project root (contains gradlew.bat)" };
        if (dlg.ShowDialog() == true && !_projects.Contains(dlg.FolderName))
        {
            _projects.Add(dlg.FolderName);
            _settings.ProjectPaths = [.. _projects]; _settings.Save();
            ProjectCombo.SelectedItem = dlg.FolderName;
        }
    }
    private void RemoveProject_Click(object s, RoutedEventArgs e)
    {
        if (ProjectCombo.SelectedItem is string sel)
        { _projects.Remove(sel); _settings.ProjectPaths = [.. _projects]; _settings.Save(); }
    }
    private void ProjectCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (ProjectCombo.SelectedItem is not string sel) return;
        _android.ProjectPath = sel;
        _settings.LastSelectedProject = sel; _settings.Save();
        Log($"\nActive project: {sel}"); LoadVariants(sel);
    }

    // ═══════════════════════════════════════════════
    //  SOURCE CONTROL  (GitHub + Branch)
    // ═══════════════════════════════════════════════
    private void LoadSourceTabFromSettings()
    {
        var cfg = _settings.CiCd;
        MainGitHubOwnerBox.Text = _env.Resolve("ACC_GITHUB_OWNER", cfg.GitHubOwner);
        MainGitHubRepoBox.Text = _env.Resolve("ACC_GITHUB_REPO", cfg.GitHubRepository);
        MainGitHubTokenBox.Password = _env.Resolve("ACC_GITHUB_TOKEN", cfg.GitHubToken);
        MainGitRemoteBox.Text = string.IsNullOrWhiteSpace(cfg.PipelineRemoteName)
            ? "origin" : cfg.PipelineRemoteName;
        MainBranchBox.Text = string.IsNullOrWhiteSpace(cfg.PipelineBranch)
            ? "master" : cfg.PipelineBranch;
        if (!string.IsNullOrWhiteSpace(MainBranchBox.Text) &&
            !_githubBranches.Contains(MainBranchBox.Text))
            _githubBranches.Add(MainBranchBox.Text);
        MainBranchCombo.SelectedItem = _githubBranches.Contains(MainBranchBox.Text)
            ? MainBranchBox.Text : null;
    }

    private void SaveSourceTabToSettings()
    {
        var cfg = _settings.CiCd;
        cfg.GitHubOwner = MainGitHubOwnerBox.Text.Trim();
        cfg.GitHubRepository = MainGitHubRepoBox.Text.Trim();
        cfg.GitHubToken = MainGitHubTokenBox.Password;
        cfg.PipelineRemoteName = string.IsNullOrWhiteSpace(MainGitRemoteBox.Text)
            ? "origin" : MainGitRemoteBox.Text.Trim();
        cfg.PipelineBranch = MainBranchBox.Text.Trim();
        _settings.Save();
    }

    private void LoadFromEnv_Click(object s, RoutedEventArgs e)
    {
        _env.Reload();
        MainGitHubOwnerBox.Text = _env.GitHubOwner;
        MainGitHubRepoBox.Text = _env.GitHubRepo;
        if (!string.IsNullOrWhiteSpace(_env.GitHubToken))
            MainGitHubTokenBox.Password = _env.GitHubToken;
        Log("Loaded secrets from .env");
    }

    private void SaveToEnv_Click(object s, RoutedEventArgs e)
    {
        _env.GitHubOwner = MainGitHubOwnerBox.Text.Trim();
        _env.GitHubRepo = MainGitHubRepoBox.Text.Trim();
        _env.GitHubToken = MainGitHubTokenBox.Password;
        _env.Save();
        Log("Secrets saved to .env");
    }

    private async void MainLoadBranches_Click(object s, RoutedEventArgs e)
    {
        if (!RequireIdle()) return;
        SaveSourceTabToSettings();
        string owner = _settings.CiCd.GitHubOwner;
        string repo = _settings.CiCd.GitHubRepository;
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        { Log("[!] Enter GitHub owner and repo first."); return; }

        _isRunning = true; _cts = new(); SetStatus("Loading branches");
        MainLoadBranchesButton.IsEnabled = false;
        try
        {
            var branches = await _github.GetBranchesAsync(
                owner, repo, _settings.CiCd.GitHubToken, _cts.Token);
            _githubBranches.Clear();
            foreach (var b in branches) _githubBranches.Add(b);
            string sel = _settings.CiCd.PipelineBranch;
            if (!string.IsNullOrWhiteSpace(sel) &&
                branches.Contains(sel, StringComparer.OrdinalIgnoreCase))
                MainBranchCombo.SelectedItem = branches.First(
                    b => string.Equals(b, sel, StringComparison.OrdinalIgnoreCase));
            else if (branches.Count > 0) MainBranchCombo.SelectedItem = branches[0];
            Log($"Loaded {branches.Count} branches.");
        }
        catch (OperationCanceledException) { Log("Cancelled."); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally { MainLoadBranchesButton.IsEnabled = true; _isRunning = false; SetStatus("Idle"); }
    }

    private void MainBranchCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (MainBranchCombo.SelectedItem is not string b) return;
        MainBranchBox.Text = b; SaveSourceTabToSettings();
    }

    private async void MainPullBranch_Click(object s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;
        SaveSourceTabToSettings();
        string remote = _settings.CiCd.PipelineRemoteName;
        string branch = _settings.CiCd.PipelineBranch;
        if (!IsSimpleGitName(remote) || !IsSimpleGitName(branch))
        { Log("[!] Enter a valid remote and branch."); return; }

        _isRunning = true; _cts = new(); SetStatus("Pulling branch");
        MainPullBranchButton.IsEnabled = false;
        try
        {
            Log($"\n===== Pull {remote}/{branch} =====");
            int code = await _git.UpdateToRemoteBranch(_android.ProjectPath, remote, branch, _cts.Token);
            Log($"===== Pull exit {code} =====");
        }
        catch (OperationCanceledException) { Log("Cancelled."); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally { MainPullBranchButton.IsEnabled = true; _isRunning = false; SetStatus("Idle"); }
    }

    // ═══════════════════════════════════════════════
    //  GITHUB ISSUES
    // ═══════════════════════════════════════════════
    private string IssueOwner => MainGitHubOwnerBox.Text.Trim();
    private string IssueRepo => MainGitHubRepoBox.Text.Trim();
    private string IssueToken => MainGitHubTokenBox.Password;

    private async void PullIssues_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(IssueOwner) || string.IsNullOrWhiteSpace(IssueRepo))
        { Log("[!] Set GitHub Owner and Repo on the Source tab first."); return; }

        string state = (IssueStateCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "open";
        Log($"\nPulling {state} issues from {IssueOwner}/{IssueRepo}…");
        try
        {
            var issues = await _issueService.GetIssuesAsync(
                IssueOwner, IssueRepo, IssueToken, state);
            _githubIssues.Clear();
            foreach (var i in issues) _githubIssues.Add(i);
            Log($"Loaded {issues.Count} issues.");
        }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
    }

    private async void IssuesGrid_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (IssuesGrid.SelectedItem is not GitHubIssueModel issue) return;
        IssueDetailHeader.Text = $"#{issue.Number}: {issue.Title}";
        IssueDetailMeta.Text =
            $"State: {issue.State}  ·  Labels: {issue.LabelsSummary}  ·  " +
            $"By @{issue.Author}  ·  {issue.CreatedDisplay}";
        IssueDetailBody.Text = issue.Body ?? "";

        _issueComments.Clear();
        IssueCommentHeader.Text = "Comments (loading…)";
        try
        {
            var comments = await _issueService.GetCommentsAsync(
                IssueOwner, IssueRepo, IssueToken, issue.Number);
            foreach (var c in comments) _issueComments.Add(c);
            IssueCommentHeader.Text = $"Comments ({comments.Count})";
        }
        catch { IssueCommentHeader.Text = "Comments (failed to load)"; }
    }

    private async void AddComment_Click(object s, RoutedEventArgs e)
    {
        if (IssuesGrid.SelectedItem is not GitHubIssueModel issue) return;
        string body = NewCommentBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(body)) { Log("[!] Enter a comment."); return; }
        bool ok = await _issueService.AddCommentAsync(
            IssueOwner, IssueRepo, IssueToken, issue.Number, body);
        if (ok) { NewCommentBox.Clear(); IssuesGrid_SelectionChanged(s, null!); }
    }

    private async void CloseIssue_Click(object s, RoutedEventArgs e)
    {
        if (IssuesGrid.SelectedItem is not GitHubIssueModel issue) return;
        if (MessageBox.Show($"Close issue #{issue.Number}?", "Confirm",
                MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (await _issueService.UpdateStateAsync(
                IssueOwner, IssueRepo, IssueToken, issue.Number, "closed"))
        { issue.State = "closed"; IssuesGrid.Items.Refresh(); }
    }

    private async void ReopenIssue_Click(object s, RoutedEventArgs e)
    {
        if (IssuesGrid.SelectedItem is not GitHubIssueModel issue) return;
        if (await _issueService.UpdateStateAsync(
                IssueOwner, IssueRepo, IssueToken, issue.Number, "open"))
        { issue.State = "open"; IssuesGrid.Items.Refresh(); }
    }

    // ═══════════════════════════════════════════════
    //  REPORTS DASHBOARD
    // ═══════════════════════════════════════════════
    private void RefreshReportsTab()
    {
        ReportsGrid.ItemsSource = _deploymentReports.All;
        var (total, ok, fail) = _deploymentReports.Stats();
        double pct = total > 0 ? ok * 100.0 / total : 0;
        var last = _deploymentReports.All.FirstOrDefault();
        ReportsSummaryLabel.Text =
            $"Deployments: {total}   ·   Passed: {ok} ({pct:F0}%)   ·   " +
            $"Failed: {fail}   ·   Last: {last?.TimestampDisplay ?? "—"}";
    }

    private void ReportsGrid_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        FailuresGrid.ItemsSource =
            (ReportsGrid.SelectedItem as DeploymentRecord)?.Failures;
    }

    private void RefreshReports_Click(object s, RoutedEventArgs e) => RefreshReportsTab();

    private void OpenSelectedLog_Click(object s, RoutedEventArgs e)
    {
        if (ReportsGrid.SelectedItem is DeploymentRecord r && File.Exists(r.LogPath))
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{r.LogPath}\"");
        else Log("[!] Select a report with a valid log path.");
    }

    private void RecordDeployment(PipelineRunResult result,
        TestReport? unit = null, TestReport? instr = null)
    {
        var record = _deploymentReports.CreateRecord(
            ProjectName(),
            _android.SelectedVariant,
            _settings.CiCd.PipelineBranch,
            result.Success, result.Status, result.LogPath,
            unit, instr);
        _deploymentReports.Add(record);
        Dispatcher.Invoke(RefreshReportsTab);
    }

    // ═══════════════════════════════════════════════
    //  VARIANTS
    // ═══════════════════════════════════════════════
    private void LoadVariants(string project)
    {
        _variants.Clear();
        foreach (var v in _settings.GetVariants(project)) _variants.Add(v);
        string prev = _settings.GetSelectedVariant(project);
        VariantCombo.SelectedItem = _variants.Contains(prev) ? prev : _variants.FirstOrDefault();
    }
    private void VariantCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (VariantCombo.SelectedItem is not string sel) return;
        _android.SelectedVariant = sel;
        if (!string.IsNullOrEmpty(_android.ProjectPath))
            _settings.SetSelectedVariant(_android.ProjectPath, sel);
    }
    private void DetectVariants_Click(object s, RoutedEventArgs e)
    {
        if (!RequireProject()) return;
        Log("\n=== Detecting variants ===");
        string? prev = VariantCombo.SelectedItem as string;
        var detected = _flavors.DetectVariants(_android.ProjectPath);
        _variants.Clear();
        foreach (var v in detected) _variants.Add(v);
        _settings.SetVariants(_android.ProjectPath, detected);
        VariantCombo.SelectedItem =
            (prev != null && _variants.Contains(prev)) ? prev : _variants.FirstOrDefault();
        Log("=== Detection complete ===");
    }

    // ═══════════════════════════════════════════════
    //  AVDs
    // ═══════════════════════════════════════════════
    private async Task LoadAvds()
    {
        try
        {
            var avds = await _avd.GetAvailableAvds(CancellationToken.None);
            _avds.Clear();
            foreach (var a in avds) _avds.Add(a);
            AvdCombo.SelectedItem =
                (_settings.PreferredAvd != null && _avds.Contains(_settings.PreferredAvd))
                    ? _settings.PreferredAvd : _avds.FirstOrDefault();
            if (_avds.Count == 0) Log("[!] No AVDs found.");
        }
        catch (Exception ex) { Log($"[WARN] AVDs: {ex.Message}"); }
    }
    private void AvdCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    { if (AvdCombo.SelectedItem is string sel) { _settings.PreferredAvd = sel; _settings.Save(); } }
    private async void RefreshAvds_Click(object s, RoutedEventArgs e) => await LoadAvds();
    private void OpenReports_Click(object s, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start("explorer.exe", TestReportService.ReportsDirectory);

    // ═══════════════════════════════════════════════
    //  COMMAND RUNNER
    // ═══════════════════════════════════════════════
    private bool RequireProject()
    { if (!string.IsNullOrEmpty(_android.ProjectPath)) return true; Log("[!] Select a project first."); return false; }
    private bool RequireIdle()
    { if (!_isRunning) return true; Log("[!] Already running."); return false; }

    private async Task RunOp(Func<CancellationToken, Task<int>> op, string name)
    {
        if (!RequireProject() || !RequireIdle()) return;
        _isRunning = true; _cts = new(); SetStatus(name);
        Log($"\n===== {name} ({_android.SelectedVariant}) =====");
        try { int c = await op(_cts.Token); Log($"===== {name} exit {c} ====="); }
        catch (OperationCanceledException) { Log($"===== {name} cancelled ====="); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally { _isRunning = false; SetStatus("Idle"); }
    }

    // ═══════════════════════════════════════════════
    //  BUILD
    // ═══════════════════════════════════════════════
    private async void Clean_Click(object s, RoutedEventArgs e) =>
        await RunOp(_android.CleanBuild, "Clean");
    private async void Build_Click(object s, RoutedEventArgs e) =>
        await RunOp(_android.AssembleVariant, $"Build {_android.SelectedVariant}");
    private async void BuildAll_Click(object s, RoutedEventArgs e) =>
        await RunOp(_android.AssembleAll, "Build All");
    private async void ListTasks_Click(object s, RoutedEventArgs e) =>
        await RunOp(_android.ListTasks, "List Tasks");

    // ═══════════════════════════════════════════════
    //  TESTS
    // ═══════════════════════════════════════════════
    private async void UnitTests_Click(object s, RoutedEventArgs e) =>
        await RunTestsWithReport(_android.RunUnitTests, "UnitTests", false);
    private async void InstrumentTests_Click(object s, RoutedEventArgs e) =>
        await RunTestsWithReport(_android.RunInstrumentTests, "InstrumentTests", true);

    private async Task RunTestsWithReport(
        Func<CancellationToken, Task<int>> testOp, string testType, bool ensureEmulator)
    {
        if (!RequireProject() || !RequireIdle()) return;
        _isRunning = true; _cts = new(); SetStatus(testType);
        var buf = new StringBuilder();
        void capture(string l) => buf.AppendLine(l);
        _runner.OutputReceived += capture;
        bool started = false;
        try
        {
            if (ensureEmulator)
            { if (await PrepareInstrumentTestTarget(_cts.Token) == null) return; }
            Log($"\n===== {testType} ({_android.SelectedVariant}) =====");
            started = true;
            int code = await testOp(_cts.Token);
            Log($"===== {testType} exit {code} =====");
            var rpt = _testReports.ParseResults(_android.ProjectPath, testType);
            ShowSummary(rpt);
            _testReports.SaveReport(rpt, _android.ProjectPath, _android.SelectedVariant, buf.ToString());
        }
        catch (OperationCanceledException) { Log($"===== {testType} cancelled ====="); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally
        {
            _runner.OutputReceived -= capture;
            if (started) await StopGradleResourcesAfterTesting(testType);
            _isRunning = false; SetStatus("Idle");
        }
    }

    private async Task StopGradleResourcesAfterTesting(string ctx, PipelineLogger? logger = null)
    {
        Log($"Stopping Gradle daemons after {ctx}…"); SetStatus("Stopping Gradle");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _android.StopDaemons(cts.Token);
            Log("Gradle daemons stopped.");
        }
        catch (Exception ex) { Log($"[WARN] {ex.Message}"); }
    }

    private async Task<string?> PrepareInstrumentTestTarget(
        CancellationToken ct, bool preferConnected = false)
    {
        string? sel = await SelectConnectedPhysicalDevice(ct, selectedOnly: true);
        if (sel != null) return sel;
        if (preferConnected)
        { sel = await SelectConnectedPhysicalDevice(ct); if (sel != null) return sel; }
        string? serial = await _avd.EnsureEmulatorReadyForTests(_settings.PreferredAvd, ct);
        if (serial == null) { Log("[ERROR] No emulator available."); return null; }
        _android.TargetDevice = serial;
        await RefreshDeviceList();
        if (_deviceSerials.Contains(serial)) DeviceCombo.SelectedItem = serial;
        return serial;
    }

    private async Task<string?> SelectConnectedPhysicalDevice(CancellationToken ct, bool selectedOnly = false)
    {
        if (selectedOnly && string.IsNullOrWhiteSpace(_android.TargetDevice)) return null;
        var devices = await _avd.GetDevicesSilent(ct);
        var physical = devices.Where(d => d.State == "device" && !d.IsEmulator).ToList();
        if (physical.Count == 0) return null;
        var pick = physical.FirstOrDefault(d =>
            string.Equals(d.Serial, _android.TargetDevice, StringComparison.OrdinalIgnoreCase));
        if (pick == null) { if (selectedOnly) return null; pick = physical[0]; }
        _android.TargetDevice = pick.Serial;
        await RefreshDeviceList();
        if (_deviceSerials.Contains(pick.Serial)) DeviceCombo.SelectedItem = pick.Serial;
        return pick.Serial;
    }

    private void ShowSummary(TestReport r)
    {
        Log("\n---------- TEST SUMMARY ----------");
        Log($"  Total: {r.Total}  Passed: {r.Passed}  Failed: {r.Failed}  Skipped: {r.Skipped}");
        foreach (var f in r.Failures)
        { Log($"   [X] {f.ClassName}.{f.Name}"); if (f.FailureMessage != null) Log($"       {f.FailureMessage}"); }
        Log("----------------------------------");
    }

    // ═══════════════════════════════════════════════
    //  ENVIRONMENT
    // ═══════════════════════════════════════════════
    private async void Devices_Click(object s, RoutedEventArgs e) =>
        await RunOp(async ct => { var (c, _) = await _android.ListDevices(ct); return c; }, "List Devices");

    private async void StartAvd_Click(object s, RoutedEventArgs e)
    {
        if (!RequireIdle()) return;
        _isRunning = true; _cts = new(); SetStatus("Starting AVD");
        try
        {
            string? serial = await _avd.EnsureEmulatorRunning(_settings.PreferredAvd, _cts.Token);
            if (serial != null) { await RefreshDeviceList(); if (_deviceSerials.Contains(serial)) DeviceCombo.SelectedItem = serial; }
        }
        catch (OperationCanceledException) { Log("Cancelled."); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally { _isRunning = false; SetStatus("Idle"); }
    }

    private async void StopDaemons_Click(object s, RoutedEventArgs e) =>
        await RunOp(_android.StopDaemons, "Stop Daemons");
    private async void RestartAdb_Click(object s, RoutedEventArgs e) =>
        await RunOp(async ct => { await _android.KillAdbServer(ct); return await _android.StartAdbServer(ct); }, "Restart ADB");

    // ═══════════════════════════════════════════════
    //  CACHE
    // ═══════════════════════════════════════════════
    private async void CacheLight_Click(object s, RoutedEventArgs e) => await RunCacheClean(CacheCleanLevel.Light);
    private async void CacheMedium_Click(object s, RoutedEventArgs e) => await RunCacheClean(CacheCleanLevel.Medium);
    private async void CacheHeavy_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show("Delete global Gradle cache?\nAll deps re-download.",
                "Heavy Clean", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            await RunCacheClean(CacheCleanLevel.Heavy);
    }
    private async void CleanLocks_Click(object s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;
        _isRunning = true; SetStatus("Cleaning locks");
        try { await Task.Run(() => _cache.CleanLockFilesOnly()); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally { _isRunning = false; SetStatus("Idle"); }
    }
    private async Task RunCacheClean(CacheCleanLevel level)
    {
        if (!RequireProject() || !RequireIdle()) return;
        _isRunning = true; _cts = new(); SetStatus($"Cache ({level})");
        Log($"\n===== Cache Clean ({level}) =====");
        try { await _cache.CleanCaches(level, _cts.Token); }
        catch (OperationCanceledException) { Log("Cancelled."); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally { _isRunning = false; SetStatus("Idle"); }
    }

    // ═══════════════════════════════════════════════
    //  FULL TEST RUN
    // ═══════════════════════════════════════════════
    private async void FullTestRun_Click(object s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;
        _isRunning = true; _cts = new(); SetStatus("Full Test Run");
        string v = _android.SelectedVariant;
        var steps = new List<WorkflowStep>
        {
            new() { Name = "Stop Daemons", Action = _android.StopDaemons, StopOnFailure = false },
            new() { Name = "Clean",        Action = _android.CleanBuild },
            new() { Name = "Invalidate Caches",
                    Action = async ct => { await _cache.CleanCaches(CacheCleanLevel.Medium, ct); return 0; } },
            new() { Name = $"Unit Tests ({v})", Action = _android.RunUnitTests, MaxRetries = 2 },
            new() { Name = "Select Instrument Target",
                    Action = async ct => (await PrepareInstrumentTestTarget(ct) != null) ? 0 : 1 },
            new() { Name = $"Instrumented Tests ({v})", Action = _android.RunInstrumentTests, MaxRetries = 2 }
        };
        try
        {
            bool ok = await _workflow.ExecuteAsync("Full Test Run", steps, _cts.Token);
            var unit = _testReports.ParseResults(_android.ProjectPath, "UnitTests");
            var inst = _testReports.ParseResults(_android.ProjectPath, "InstrumentTests");
            if (unit.Total > 0) { ShowSummary(unit); _testReports.SaveReport(unit, _android.ProjectPath, v, "(workflow)"); }
            if (inst.Total > 0) { ShowSummary(inst); _testReports.SaveReport(inst, _android.ProjectPath, v, "(workflow)"); }
            await RefreshDeviceList();
            Log(ok ? "\nAll steps passed!" : "\nSome steps failed.");
        }
        catch (OperationCanceledException) { Log("\nWorkflow cancelled."); }
        catch (Exception ex) { Log($"\n[ERROR] {ex.Message}"); }
        finally
        {
            await StopGradleResourcesAfterTesting("full test run");
            _isRunning = false; SetStatus("Idle");
        }
    }

    // ═══════════════════════════════════════════════
    //  CI / CD
    // ═══════════════════════════════════════════════
    private string ProjectName() =>
        Path.GetFileName(_android.ProjectPath.TrimEnd('\\', '/'));

    private void UpdateCiCdButtonStates()
    {
        bool fb = _settings.CiCd.IsFirebaseConfigured;
        DeployFirebaseButton.IsEnabled = fb;
        DeployFirebaseButton.ToolTip = fb
            ? "Upload APK to Firebase App Distribution"
            : "Complete Firebase Config first.";
    }

    private void FirebaseConfig_Click(object s, RoutedEventArgs e)
    {
        var dlg = new FirebaseConfigWindow(_settings.CiCd) { Owner = this };
        if (dlg.ShowDialog() == true)
        { _firebase.CliPath = _settings.CiCd.FirebaseCliPath; _settings.Save(); UpdateCiCdButtonStates(); Log("Firebase config saved."); }
    }

    private async void BuildSigned_Click(object s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;
        var cfg = _settings.CiCd;
        var dlg = new BuildSignedApkWindow(cfg, _variants, _android.SelectedVariant) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _settings.Save();
        var logger = new PipelineLogger("BSA", ProjectName());
        _pipelineLogger = logger; _isRunning = true; _cts = new(); SetStatus("Build Signed APK");
        try
        {
            logger.Section("BUILD SIGNED APK");
            int code = await _android.BuildSignedApk(cfg, cfg.BuildVariants, _cts.Token);
            Log($"Build exit {code}");
            if (code == 0) { var apk = FindApk(); Log(apk != null ? $"APK: {apk}" : "[WARN] APK not found."); }
        }
        catch (OperationCanceledException) { Log("Cancelled."); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally { logger.Save(); _pipelineLogger = null; _isRunning = false; SetStatus("Idle"); }
    }

    private async void DeployFirebase_Click(object s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;
        var cfg = _settings.CiCd;
        var dlg = new DeployFirebaseWindow(cfg, FindApk()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _settings.Save();
        var apk = dlg.ApkPath;
        if (!File.Exists(apk)) { Log("[!] Select a valid APK."); return; }
        var logger = new PipelineLogger("DEPLOY", ProjectName());
        _pipelineLogger = logger; _isRunning = true; _cts = new(); SetStatus("Deploying");
        try
        {
            _firebase.CliPath = cfg.FirebaseCliPath;
            int code = await _firebase.Distribute(
                apk, cfg.FirebaseAppId, cfg.TesterGroups, cfg.TesterEmails,
                cfg.ReleaseNotes, cfg.FirebaseToken, cfg.CredentialsJsonPath, _cts.Token);
            Log(code == 0 ? "Upload successful!" : $"Deploy exit {code}");

            // record to reports
            var result = new PipelineRunResult(code == 0,
                code == 0 ? "Deployed" : $"Deploy exit {code}", logger.FilePath);
            RecordDeployment(result);
        }
        catch (OperationCanceledException) { Log("Cancelled."); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally { logger.Save(); _pipelineLogger = null; _isRunning = false; SetStatus("Idle"); }
    }

    private async void AutomationConfig_Click(object s, RoutedEventArgs e)
    {
        var dlg = new AutomationWindow(_settings.Automation, _settings.CiCd) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _settings.Save(); LoadSourceTabFromSettings(); Log("Automation config saved.");
        if (dlg.RunNow) await RunAutomatedDeployment(false);
    }

    private async void RunAutomatedDeployment_Click(object s, RoutedEventArgs e) =>
        await RunAutomatedDeployment(false);

    private async void FullPipeline_Click(object s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;
        var cfg = _settings.CiCd;
        var dlg = new FullPipelineWindow(cfg, _variants, _android.SelectedVariant) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _settings.Save(); LoadSourceTabFromSettings(); UpdateCiCdButtonStates();

        Func<PipelineLogger, CancellationToken, Task<bool>>? pre = null;
        if (cfg.SyncBranchBeforePipeline)
            pre = async (l, ct) =>
            {
                l.Line($"Remote: {cfg.PipelineRemoteName}  Branch: {cfg.PipelineBranch}");
                return await _git.UpdateToRemoteBranch(
                    _android.ProjectPath, cfg.PipelineRemoteName, cfg.PipelineBranch, ct) == 0;
            };
        await RunFullPipeline(cfg, pre);
    }

    private async Task RunAutomatedDeployment(bool triggeredBySchedule)
    {
        if (!RequireProject() || !RequireIdle()) return;
        var auto = CloneAutomationConfig(_settings.Automation);
        var cfg = CloneCiCdConfig(_settings.CiCd);
        cfg.FailureAction = auto.FailureAction;
        if (cfg.BuildVariants.Count == 0 && !string.IsNullOrWhiteSpace(_android.SelectedVariant))
            cfg.BuildVariants = [_android.SelectedVariant];
        if (!ValidateAutomatedDeploymentConfig(cfg, auto)) return;

        var result = await RunFullPipeline(cfg,
            async (l, ct) =>
            {
                l.Line($"Remote: {auto.RemoteName}  Branch: {auto.SourceBranch}");
                return await _git.UpdateToRemoteBranch(
                    _android.ProjectPath, auto.RemoteName, auto.SourceBranch, ct) == 0;
            }, "Automated Pipeline");
        await SendAutomationNotification(auto, result, !triggeredBySchedule);
    }

    // ── Pipeline core ────────────────────────────
    private async Task<PipelineRunResult> RunFullPipeline(
        CiCdConfig cfg,
        Func<PipelineLogger, CancellationToken, Task<bool>>? preflight = null,
        string statusText = "Full Pipeline")
    {
        var logger = new PipelineLogger("FP", ProjectName());
        _pipelineLogger = logger; _isRunning = true; _cts = new();
        SetStatus(statusText); var ct = _cts.Token;
        bool testsStarted = false, cleanupDone = false;
        TestReport? unitRpt = null, instrRpt = null;

        try
        {
            if (preflight != null)
            {
                logger.Section("SYNC SOURCE");
                if (!await preflight(logger, ct))
                    return Finish(false, "Source update failed");
            }

            logger.Section("STOP DAEMONS"); await _android.StopDaemons(ct);
            logger.Section("CLEAN");
            if (await _android.CleanBuild(ct) != 0) return Finish(false, "Clean failed");

            if (cfg.RunUnitTests)
            {
                testsStarted = true; logger.Section("UNIT TESTS");
                int c = await _android.RunUnitTests(ct);
                unitRpt = _testReports.ParseResults(_android.ProjectPath, "UnitTests");
                ShowSummary(unitRpt);
                _testReports.SaveReport(unitRpt, _android.ProjectPath, _android.SelectedVariant, "(FP)");
                if ((unitRpt.Failed > 0 || c != 0) &&
                    !await HandleTestFailure("Unit Tests", unitRpt, cfg.FailureAction, logger))
                    return Finish(false, "Stopped after unit test failure");
            }

            if (cfg.RunInstrumentTests)
            {
                testsStarted = true;
                logger.Section("SELECT INSTRUMENT TARGET");
                if (await PrepareInstrumentTestTarget(ct, true) == null)
                    return Finish(false, "No instrument test target");
                logger.Section("INSTRUMENT TESTS");
                int c = await _android.RunInstrumentTests(ct);
                instrRpt = _testReports.ParseResults(_android.ProjectPath, "InstrumentTests");
                ShowSummary(instrRpt);
                _testReports.SaveReport(instrRpt, _android.ProjectPath, _android.SelectedVariant, "(FP)");
                if ((instrRpt.Failed > 0 || c != 0) &&
                    !await HandleTestFailure("Instrument Tests", instrRpt, cfg.FailureAction, logger))
                    return Finish(false, "Stopped after instrument test failure");
            }

            if (testsStarted)
            { await StopGradleResourcesAfterTesting("pipeline tests", logger); cleanupDone = true; SetStatus(statusText); }

            logger.Section("BUILD SIGNED APK");
            if (await _android.BuildSignedApk(cfg, cfg.BuildVariants, ct) != 0)
                return Finish(false, "Build failed");
            var apk = FindApk();
            if (apk == null) return Finish(false, "APK not found after build");
            Log($"APK: {apk}");

            logger.Section("DEPLOY TO FIREBASE");
            _firebase.CliPath = cfg.FirebaseCliPath;
            int deploy = await _firebase.Distribute(
                apk, cfg.FirebaseAppId, cfg.TesterGroups, cfg.TesterEmails,
                cfg.ReleaseNotes, cfg.FirebaseToken, cfg.CredentialsJsonPath, ct);
            bool ok = deploy == 0;
            Log(ok ? "\nPIPELINE COMPLETE" : $"\nDeploy returned {deploy}");
            return Finish(ok, ok ? "Deployed successfully" : $"Deploy exit {deploy}");
        }
        catch (OperationCanceledException) { Log("\nPipeline cancelled."); return Finish(false, "Cancelled"); }
        catch (Exception ex) { Log($"\n[ERROR] {ex.Message}"); return Finish(false, ex.Message); }
        finally
        {
            if (testsStarted && !cleanupDone) await StopGradleResourcesAfterTesting("pipeline tests", logger);
            await RefreshDeviceList();
            logger.Save(); Log($"Pipeline log: {logger.FilePath}");
            _pipelineLogger = null; _isRunning = false; SetStatus("Idle");
        }

        PipelineRunResult Finish(bool success, string status)
        {
            var r = new PipelineRunResult(success, status, logger.FilePath);
            RecordDeployment(r, unitRpt, instrRpt);
            return r;
        }
    }

    private sealed record PipelineRunResult(bool Success, string Status, string LogPath);

    private async Task<bool> HandleTestFailure(
        string stage, TestReport report, TestFailureAction action, PipelineLogger logger)
    {
        string det = BuildFailureDetails(report);
        logger.Line($"[{stage}] {report.Failed}/{report.Total} failed.\n{det}");
        switch (action)
        {
            case TestFailureAction.SilentFailure:
                Log("[Policy] Silent — continuing."); return true;
            case TestFailureAction.AutomaticBounceBack:
                Log("[Policy] Bounce back — stopping."); return false;
            case TestFailureAction.Warning:
            case TestFailureAction.WarningWithConfirm:
                bool confirm = action == TestFailureAction.WarningWithConfirm;
                var dlg = new TestFailureDialog($"{stage} Failed", det, confirm) { Owner = this };
                bool proceed = dlg.ShowDialog() == true;
                Log($"[Policy] User chose to {(proceed ? "CONTINUE" : "STOP")}."); return proceed;
        }
        return false;
    }

    private static string BuildFailureDetails(TestReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Failed {r.Failed} of {r.Total}.");
        foreach (var f in r.Failures)
        {
            sb.AppendLine($"\n[X] {f.ClassName}.{f.Name}");
            if (f.FailureType != null) sb.AppendLine($"    Type: {f.FailureType}");
            if (f.FailureMessage != null) sb.AppendLine($"    Msg:  {f.FailureMessage}");
            if (!string.IsNullOrWhiteSpace(f.StackTrace))
                foreach (var line in f.StackTrace.Split('\n').Take(15))
                    if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine($"      {line.TrimEnd()}");
        }
        return sb.ToString();
    }

    private string? FindApk()
    {
        var root = Path.Combine(_android.ProjectPath, "app", "build", "outputs", "apk");
        if (!Directory.Exists(root)) return null;
        return Directory.GetFiles(root, "*.apk", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Contains("unsigned"))
            .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
    }

    // ── Automation helpers (unchanged logic) ─────
    private bool ValidateAutomatedDeploymentConfig(CiCdConfig cfg, AutomationConfig auto)
    {
        if (!cfg.IsSigningConfigured) { Log("[!] Signing not configured."); return false; }
        if (!cfg.IsFirebaseConfigured) { Log("[!] Firebase not configured."); return false; }
        if (cfg.BuildVariants.Count == 0) { Log("[!] No build variants."); return false; }
        if (auto.Email.SendEmail && !auto.CanSendEmail) { Log("[!] Email settings incomplete."); return false; }
        return true;
    }

    private async Task SendAutomationNotification(
        AutomationConfig auto, PipelineRunResult result, bool showLocal)
    {
        if (!auto.Email.SendEmail)
        {
            if (showLocal)
                MessageBox.Show($"Finished: {result.Status}\n\nLog:\n{result.LogPath}",
                    "Automation", MessageBoxButton.OK,
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            else
                Log($"Automation finished: {result.Status}");
            return;
        }
        _isRunning = true; SetStatus("Sending notification");
        try
        {
            await _emailNotifications.SendPipelineLog(
                auto, ProjectName(), result.Success, result.Status,
                result.LogPath, CancellationToken.None);
        }
        catch (Exception ex) { Log($"[WARN] Email failed: {ex.Message}"); }
        finally { _isRunning = false; SetStatus("Idle"); }
    }

    private async Task CheckScheduledAutomation()
    {
        if (_isRunning || !_settings.Automation.RecurringRunsEnabled) return;
        string? dueKey = GetDueScheduledRunKey(_settings.Automation, DateTime.Now);
        if (dueKey == null) return;
        _settings.Automation.LastScheduledRunKey = dueKey; _settings.Save();
        Log($"\nScheduled run triggered: {dueKey}");
        await RunAutomatedDeployment(true);
    }

    private static string? GetDueScheduledRunKey(AutomationConfig a, DateTime now)
    {
        if (!TryParseRecurringTimes(a.RecurringRunTimes, out var times)) return null;
        foreach (var t in times)
        {
            var at = now.Date.Add(t);
            var age = now - at;
            if (age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(5))
            { string k = $"{at:yyyy-MM-dd}|{t:hh\\:mm}"; return a.LastScheduledRunKey == k ? null : k; }
        }
        return null;
    }

    private static bool TryParseRecurringTimes(string v, out List<TimeSpan> times)
    {
        times = []; var seen = new HashSet<TimeSpan>();
        foreach (string raw in v.Split([',', ';'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TimeSpan.TryParseExact(raw, [@"h\:mm", @"hh\:mm"],
                    CultureInfo.InvariantCulture, out var t) ||
                t < TimeSpan.Zero || t >= TimeSpan.FromDays(1)) return false;
            if (seen.Add(t)) times.Add(t);
        }
        times.Sort(); return times.Count > 0;
    }

    private static CiCdConfig CloneCiCdConfig(CiCdConfig s) => new()
    {
        KeystorePath = s.KeystorePath,
        KeyAlias = s.KeyAlias,
        EncryptedStorePassword = s.EncryptedStorePassword,
        EncryptedKeyPassword = s.EncryptedKeyPassword,
        BuildVariants = s.BuildVariants.ToList(),
        FirebaseCliPath = s.FirebaseCliPath,
        FirebaseAppId = s.FirebaseAppId,
        CredentialsJsonPath = s.CredentialsJsonPath,
        EncryptedFirebaseToken = s.EncryptedFirebaseToken,
        TesterGroups = s.TesterGroups,
        TesterEmails = s.TesterEmails,
        ReleaseNotes = s.ReleaseNotes,
        RunUnitTests = s.RunUnitTests,
        RunInstrumentTests = s.RunInstrumentTests,
        FailureAction = s.FailureAction,
        GitHubOwner = s.GitHubOwner,
        GitHubRepository = s.GitHubRepository,
        EncryptedGitHubToken = s.EncryptedGitHubToken,
        PipelineRemoteName = s.PipelineRemoteName,
        PipelineBranch = s.PipelineBranch,
        SyncBranchBeforePipeline = s.SyncBranchBeforePipeline
    };

    private static AutomationConfig CloneAutomationConfig(AutomationConfig s) => new()
    {
        RemoteName = s.RemoteName,
        SourceBranch = s.SourceBranch,
        RecurringRunsEnabled = s.RecurringRunsEnabled,
        RecurringRunTimes = s.RecurringRunTimes,
        LastScheduledRunKey = s.LastScheduledRunKey,
        FailureAction = s.FailureAction,
        Email = new EmailNotificationConfig
        {
            SendEmail = s.Email.SendEmail,
            SmtpHost = s.Email.SmtpHost,
            SmtpPort = s.Email.SmtpPort,
            EnableSsl = s.Email.EnableSsl,
            UserName = s.Email.UserName,
            EncryptedPassword = s.Email.EncryptedPassword,
            FromAddress = s.Email.FromAddress,
            ToAddress = s.Email.ToAddress
        }
    };

    // ═══════════════════════════════════════════════
    //  UTILITY
    // ═══════════════════════════════════════════════
    private void Cancel_Click(object s, RoutedEventArgs e)
    { _cts?.Cancel(); Log("[!] Cancel requested…"); }
    private void ClearLog_Click(object s, RoutedEventArgs e) => LogBox.Clear();

    private static bool IsSimpleGitName(string v)
    {
        v = v.Trim();
        return !string.IsNullOrWhiteSpace(v) && !v.Any(char.IsWhiteSpace)
            && !v.Contains('"') && !v.Contains('\'');
    }
}