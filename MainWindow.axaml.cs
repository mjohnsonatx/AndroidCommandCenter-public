using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AndroidCommandCenter.Models;
using AndroidCommandCenter.Services;
using AndroidCommandCenter.UI;

namespace AndroidCommandCenter;

public partial class MainWindow : Window
{
    #region Services and state

    private readonly ProcessRunner _runner = new();
    private readonly SecretStore _secrets = new();
    private readonly AndroidService _android;
    private readonly AvdManager _avd;
    private readonly GitService _git;
    private readonly GitHubService _github = new();
    private readonly CacheManager _cache;
    private readonly WorkflowEngine _workflow = new();
    private readonly FlavorDetector _flavors = new();
    private readonly DashboardService _dashboard = new();
    private readonly TestReportService _testReports = new();
    private readonly EmailNotificationService _emailNotifications;
    private readonly FirebaseService _firebase;
    private readonly EnvFileService _env = new(Paths.EnvFile);
    private readonly GitHubIssueRestService _issueService = new();
    private readonly GitHubCliCredentialService _ghCredentials = new();
    private readonly DeploymentReportService _deploymentReports = new();
    private readonly ProjectProfileService _profiles;
    private readonly AppSettings _settings;
    private readonly ConfigGate _configGate = new();
    private readonly Toaster _toasts = new();

    private PipelineLogger? _pipelineLogger;
    private ProjectProfile _activeProfile = new();

    private readonly ObservableCollection<string> _projects = new();
    private readonly ObservableCollection<string> _variants = new();
    private readonly ObservableCollection<string> _avds = new();
    private readonly ObservableCollection<string> _deviceSerials = new();
    private readonly ObservableCollection<string> _authorizedDevices = new();
    private readonly ObservableCollection<string> _githubBranches = new();
    private readonly ObservableCollection<GitHubIssueModel> _githubIssues = new();
    private readonly ObservableCollection<GitHubCommentDto> _issueComments = new();
    private readonly ObservableCollection<GitHubRepoDto> _githubRepos = new();

    private readonly DispatcherTimer _deviceTimer;
    private readonly DispatcherTimer _automationTimer;

    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private string _operationName = "";
    private int _operationCurrent;
    private int _operationTotal;
    private bool _deviceRefreshing;
    private bool _uiReady;

    private const string AllDevices = "(All connected)";

    private static readonly SolidColorBrush IdleBrush = new(Color.Parse("#a6e3a1"));
    private static readonly SolidColorBrush BusyBrush = new(Color.Parse("#f9e2af"));

    #endregion

    #region Construction

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load(m => Log(m));
        _profiles = new ProjectProfileService(_secrets);
        _android = new AndroidService(_runner);
        _avd = new AvdManager(_runner, _android.AdbPath, _android.EmulatorPath);
        _git = new GitService(_runner);
        _cache = new CacheManager(_runner, _android);
        _firebase = new FirebaseService(_runner);
        _emailNotifications = new EmailNotificationService(_secrets);

        _toasts.LogSink = m => Dispatcher.UIThread.Post(() => AppendLog(m));
        Toasts.Toaster = _toasts;

        // Every service funnels its log through here.
        foreach (var hook in new Action<Action<string>>[]
        {
            h => _runner.OutputReceived += h,
            h => _android.LogMessage += h,
            h => _firebase.LogMessage += h,
            h => _avd.LogMessage += h,
            h => _git.LogMessage += h,
            h => _github.LogMessage += h,
            h => _cache.LogMessage += h,
            h => _workflow.LogMessage += h,
            h => _flavors.LogMessage += h,
            h => _testReports.LogMessage += h,
            h => _emailNotifications.LogMessage += h,
            h => _env.LogMessage += h,
            h => _issueService.LogMessage += h,
            h => _profiles.LogMessage += h,
        }) hook(L);

        _dashboard.StatsUpdated += s => Dispatcher.UIThread.Post(() => UpdateDashboard(s));
        _dashboard.Degraded += m => _toasts.Warning("System monitor unavailable", m);
        _workflow.ProgressChanged += p => Dispatcher.UIThread.Post(() =>
            ReportOperationProgress(
                p.Current, p.Total, p.Step,
                p.State is not WorkflowProgressState.Running,
                p.State == WorkflowProgressState.Failed ? "failed" : null));
        _dashboard.Start();

        ProjectCombo.ItemsSource = _projects;
        VariantCombo.ItemsSource = _variants;
        AvdCombo.ItemsSource = _avds;
        DeviceCombo.ItemsSource = _deviceSerials;
        AuthorizedDevicesList.ItemsSource = _authorizedDevices;
        MainBranchCombo.ItemsSource = _githubBranches;
        IssuesGrid.ItemsSource = _githubIssues;
        IssueCommentsPanel.ItemsSource = _issueComments;
        MainGitHubRepoCombo.ItemsSource = _githubRepos;
        MainGitHubRepoCombo.DisplayMemberBinding =
            new Avalonia.Data.Binding(nameof(GitHubRepoDto.Display));

        foreach (var p in _settings.ProjectPaths) _projects.Add(p);

        _deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _deviceTimer.Tick += async (_, _) => await RefreshDeviceList();

        _automationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _automationTimer.Tick += async (_, _) =>
        {
            await CheckScheduledAutomation();
            await CheckScheduledPromotion();
        };

        Opened += OnOpened;
    }

    private async void OnOpened(object? s, EventArgs e)
    {
        _uiReady = true;
        Log("Android Command Center ready.");
        Log($"SDK:      {_android.SdkPath}");
        Log($"Java:     {_android.JavaEnvironment.ShortDisplay}");
        Log($"Config:   {Paths.ConfigDir}");
        Log($"Logs:     {Paths.StateDir}");
        Log($"Secrets:  {SecretStore.BackendDescription}");

        CtxSecretsLabel.Text = SecretStore.ActiveBackend switch
        {
            SecretStore.Backend.Keyring => "✓ keyring",
            SecretStore.Backend.EncryptedFile => "⚠ file (degraded)",
            _ => "✗ unavailable"
        };
        UpdateContextBanner();

        if (_settings.LoadError != null)
            _toasts.Warning("Settings could not be read", _settings.LoadError);

        if (SecretStore.ActiveBackend == SecretStore.Backend.EncryptedFile)
            _toasts.Warning("Keyring unavailable",
                "Secrets are stored in an encrypted file protected only by file permissions. " +
                "Install/start gnome-keyring for proper protection.",
                details: "Checked for `secret-tool` and a reachable libsecret daemon. " +
                         $"Fallback file: {Path.Combine(Paths.ConfigDir, "secrets.enc")}");

        // Environment problems up front, not 10 minutes into a build.
        var problems = _android.Diagnose();
        if (problems.Count > 0)
        {
            foreach (var p in problems) Log($"[ENV] {p}");
            _toasts.Warning($"{problems.Count} environment problem(s)",
                "Some tools are missing — related actions will fail.",
                details: string.Join('\n', problems),
                actionLabel: "Details", action: () => { });
        }

        if (_settings.NeedsCredentialReentry)
        {
            await Dialogs.WarningAsync(this, "Credentials need re-entry",
                "Your settings were created on Windows. The stored passwords were encrypted with " +
                "Windows DPAPI and cannot be decrypted here.\n\n" +
                "Re-enter your keystore passwords, GitHub token, and SMTP password once; they will " +
                "be saved to the Linux keyring.");
            _settings.ClearLegacyCredentials();
        }

        RefreshReportsTab();

        if (_settings.LastSelectedProject is { } last && _projects.Contains(last))
            ProjectCombo.SelectedItem = last;
        else if (_projects.Count > 0)
            ProjectCombo.SelectedIndex = 0;
        else
            _toasts.Info("No project yet", "Click + in the top bar to add an Android project folder.");

        _deviceTimer.Start();
        _automationTimer.Start();
        await LoadAvds();
        await RefreshDeviceList();
    }

    private void L(string msg) => Dispatcher.UIThread.Post(() => AppendLog(msg));

    protected override void OnClosed(EventArgs e)
    {
        _deviceTimer.Stop();
        _automationTimer.Stop();
        _cts?.Cancel();
        _dashboard.Dispose();
        base.OnClosed(e);
    }

    #endregion

    #region Logging and status

    private const int MaxLogChars = 400_000;

    private void AppendLog(string msg)
    {
        // Unbounded TextBox growth makes the window unusable after a long build.
        if (LogBox.Text is { Length: > MaxLogChars } existing)
            LogBox.Text = "…(trimmed)…\n" + existing[(existing.Length - MaxLogChars / 2)..];

        LogBox.Text += msg + Environment.NewLine;
        LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        _pipelineLogger?.Line(msg);
    }

    private void Log(string msg) => AppendLog(msg);

    private void SetStatus(string text)
    {
        StatusLabel.Text = text;
        StatusLabel.Foreground = text == "Idle" ? IdleBrush : BusyBrush;
        BusySpinner.IsVisible = text != "Idle" && _operationTotal <= 0;
    }

    private void ReportOperationProgress(
        int current, int total, string phase, bool completed = false, string? detail = null)
    {
        if (!_isRunning) return;

        _operationTotal = Math.Max(1, total);
        _operationCurrent = Math.Clamp(current, 1, _operationTotal);
        OperationProgressBar.IsVisible = true;
        OperationProgressBar.IsIndeterminate = _operationTotal == 1 && !completed;
        OperationProgressBar.Maximum = _operationTotal;
        OperationProgressBar.Value = completed ? _operationCurrent : _operationCurrent - 1;
        OperationProgressLabel.IsVisible = true;
        OperationProgressLabel.Text = $"{_operationCurrent} of {_operationTotal} · {phase}" +
                                      (string.IsNullOrWhiteSpace(detail) ? "" : $" · {detail}");
        BusySpinner.IsVisible = false;
        SetStatus(phase);
    }

    private void ReportTestResults(string testType, TestReport report)
    {
        if (report.Total <= 0) return;
        ReportOperationProgress(
            Math.Max(1, _operationCurrent), Math.Max(1, _operationTotal),
            testType, completed: true,
            detail: $"tests: {report.Passed} passed of {report.Total}; " +
                    $"{report.Failed} failed, {report.Skipped} skipped");
    }

    private void UpdateDashboard(SystemStats s)
    {
        CpuBar.Value = s.CpuPercent;
        CpuLabel.Text = $"{s.CpuPercent:F0}%";
        MemBar.Value = s.MemoryPercent;
        MemLabel.Text = s.MemoryTotalMb > 0
            ? $"{s.MemoryUsedMb / 1024.0:F1}/{s.MemoryTotalMb / 1024.0:F1} GB"
            : "—";
        DaemonLabel.Text = s.JavaProcessCount.ToString();
    }

    private void UpdateContextBanner()
    {
        bool hasProject = !string.IsNullOrEmpty(_android.ProjectPath);
        CtxProjectLabel.Text = !hasProject
            ? "(no project)"
            : Path.GetFileName(_android.ProjectPath.TrimEnd('/', '\\'));
        CtxRepoLabel.Text = !string.IsNullOrWhiteSpace(_activeProfile.GitHubRepo)
            ? $"{_activeProfile.GitHubOwner}/{_activeProfile.GitHubRepo}" : "—";
        CtxBranchLabel.Text = !string.IsNullOrWhiteSpace(_activeProfile.DefaultBranch)
            ? _activeProfile.DefaultBranch : "—";
        CtxSigningLabel.Text = _profiles.HasCompleteSigning(_activeProfile) ? "✓ ready" : "✗ not set";
        CtxFirebaseLabel.Text = _activeProfile.HasFirebase ? "✓ configured" : "✗ not set";
        CtxJdkLabel.Text = _android.JavaEnvironment.ShortDisplay;
        ToolTip.SetTip(CtxJdkLabel, _android.JavaEnvironment.Report());
        NoProjectBanner.IsVisible = !hasProject;
    }

    #endregion

    #region Operation host — busy state + toasts

    private bool RequireProject()
    {
        if (!string.IsNullOrEmpty(_android.ProjectPath)) return true;
        _toasts.Warning("No project selected", "Add or select an Android project in the top bar first.");
        return false;
    }

    private bool RequireIdle()
    {
        if (!_isRunning) return true;
        _toasts.Warning("Already running", $"'{StatusLabel.Text}' is in progress. Cancel it first.");
        return false;
    }

    private void EnterBusy(string status, int totalSteps = 1)
    {
        _isRunning = true;
        _operationName = status;
        _operationCurrent = 0;
        _operationTotal = Math.Max(0, totalSteps);
        _cts = new CancellationTokenSource();
        SidebarOps.IsEnabled = false;
        ProjectCombo.IsEnabled = false;
        VariantCombo.IsEnabled = false;
        AvdCombo.IsEnabled = false;
        DeviceCombo.IsEnabled = false;
        CancelButton.IsEnabled = true;
        TopAbortButton.IsVisible = true;
        TopAbortButton.IsEnabled = true;
        OperationProgressBar.IsVisible = totalSteps > 0;
        OperationProgressLabel.IsVisible = true;
        if (totalSteps > 0)
            ReportOperationProgress(1, totalSteps, status);
        else
        {
            OperationProgressBar.IsIndeterminate = true;
            OperationProgressLabel.Text = status;
            SetStatus(status);
        }
    }

    private void ExitBusy()
    {
        _isRunning = false;
        SidebarOps.IsEnabled = true;
        ProjectCombo.IsEnabled = true;
        VariantCombo.IsEnabled = true;
        AvdCombo.IsEnabled = true;
        DeviceCombo.IsEnabled = true;
        CancelButton.IsEnabled = false;
        TopAbortButton.IsEnabled = false;
        TopAbortButton.IsVisible = false;
        OperationProgressBar.IsVisible = false;
        OperationProgressBar.Value = 0;
        OperationProgressLabel.IsVisible = false;
        OperationProgressLabel.Text = "";
        _operationName = "";
        _operationCurrent = 0;
        _operationTotal = 0;
        SetStatus("Idle");
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>
    /// Runs a gradle/adb style operation with full busy-state management and a
    /// terminal toast. Every sidebar action goes through here so that no
    /// operation can ever finish without visible feedback.
    /// </summary>
    private async Task RunOp(
        Func<CancellationToken, Task<int>> op,
        string name,
        bool requireProject = true,
        string? successMessage = null)
    {
        if (requireProject && !RequireProject()) return;
        if (!RequireIdle()) return;

        EnterBusy(name);
        var tail = new Queue<string>();
        void capture(string line) { if (tail.Count >= 60) tail.Dequeue(); tail.Enqueue(line); }
        _runner.OutputReceived += capture;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Log($"\n===== {name} =====");
        try
        {
            int code = await op(_cts!.Token);
            ReportOperationProgress(1, 1, name, completed: true,
                detail: code == 0 ? "completed" : $"exit {code}");
            sw.Stop();
            Log($"===== {name} exit {code} ({sw.Elapsed:mm\\:ss}) =====");

            if (code == 0)
                _toasts.Success(name + " succeeded",
                    successMessage ?? $"Completed in {sw.Elapsed:mm\\:ss}.");
            else
                _toasts.Error(name + " failed", $"Exit code {code} after {sw.Elapsed:mm\\:ss}.",
                    details: string.Join('\n', tail));
        }
        catch (OperationCanceledException)
        {
            Log($"===== {name} cancelled =====");
            _toasts.Warning(name + " cancelled", "Stopped at your request.");
        }
        catch (ToolLaunchException ex)
        {
            Log($"[ERROR] {ex.Message}");
            _toasts.Error($"Cannot run {ex.Tool}", ex.Message,
                details: "Check that the tool is installed and that ANDROID_HOME points at your SDK.");
        }
        catch (Exception ex)
        {
            Log($"[ERROR] {ex.Message}");
            _toasts.Error(name + " failed", Toaster.Friendly(ex), details: ex.ToString());
        }
        finally
        {
            _runner.OutputReceived -= capture;
            ExitBusy();
        }
    }

    /// <summary>Non-gradle async work (HTTP, git helpers) with the same guarantees.</summary>
    private async Task RunTask(Func<CancellationToken, Task> work, string name,
        string? successMessage = null, bool requireProject = false)
    {
        if (requireProject && !RequireProject()) return;
        if (!RequireIdle()) return;

        EnterBusy(name);
        try
        {
            await work(_cts!.Token);
            ReportOperationProgress(1, 1, name, completed: true, detail: "completed");
            if (successMessage != null) _toasts.Success(name, successMessage);
        }
        catch (OperationCanceledException) { _toasts.Warning(name + " cancelled"); }
        catch (Exception ex)
        {
            Log($"[ERROR] {ex.Message}");
            _toasts.Error(name + " failed", Toaster.Friendly(ex), details: ex.ToString());
        }
        finally { ExitBusy(); }
    }

    private void Cancel_Click(object? s, RoutedEventArgs e)
    {
        if (_cts is null) { _toasts.Info("Nothing to cancel"); return; }
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        Log("[!] Cancel requested…");
        CancelButton.IsEnabled = false;
        TopAbortButton.IsEnabled = false;
        OperationProgressBar.IsIndeterminate = true;
        OperationProgressBar.IsVisible = true;
        OperationProgressLabel.IsVisible = true;
        OperationProgressLabel.Text = $"Abort requested · stopping {_operationName} process tree…";
        _operationTotal = 0;
        SetStatus("Aborting");
    }

    private void ClearLog_Click(object? s, RoutedEventArgs e)
    {
        LogBox.Text = "";
        _toasts.Clear();
    }

    #endregion

    #region Projects and profiles

    private async void AddProject_Click(object? s, RoutedEventArgs e)
    {
        var folder = await Dialogs.PickFolderAsync(this, "Select Android project root (contains gradlew)");
        if (folder is null) return;

        if (Platform.GradleWrapper(folder) is null &&
            !await Dialogs.ConfirmAsync(this, "No Gradle wrapper found",
                $"'{folder}' has no 'gradlew' file, so builds and tests will fail.\n\nAdd it anyway?",
                yes: "Add anyway"))
            return;

        if (_projects.Contains(folder))
        {
            _toasts.Info("Already added", folder);
            ProjectCombo.SelectedItem = folder;
            return;
        }

        _projects.Add(folder);
        _settings.ProjectPaths = [.. _projects];
        _settings.Save();
        ProjectCombo.SelectedItem = folder;
        _toasts.Success("Project added", Path.GetFileName(folder.TrimEnd('/')));
    }

    private async void RemoveProject_Click(object? s, RoutedEventArgs e)
    {
        if (ProjectCombo.SelectedItem is not string sel)
        { _toasts.Info("Select a project to remove first."); return; }

        if (!await Dialogs.ConfirmAsync(this, "Remove project from list?",
                $"{sel}\n\nThis only removes it from Android Command Center. " +
                "Files on disk and saved credentials are untouched."))
            return;

        _projects.Remove(sel);
        _settings.ProjectPaths = [.. _projects];
        _settings.Save();
        _toasts.Success("Project removed", Path.GetFileName(sel.TrimEnd('/')));
    }

    private void ProjectCombo_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (!_uiReady || ProjectCombo.SelectedItem is not string sel) return;

        _android.ProjectPath = sel;
        _settings.LastSelectedProject = sel;
        _settings.Save();
        Log($"\nActive project: {sel}");

        try
        {
            LoadVariants(sel);
            RefreshAuthorizedDevices();
            ActivateProfileForProject(sel);
        }
        catch (Exception ex)
        {
            _toasts.Error("Could not load project profile", Toaster.Friendly(ex), details: ex.ToString());
        }
    }

    private void ActivateProfileForProject(string projectPath)
    {
        _activeProfile = _profiles.GetOrCreate(projectPath);
        _profiles.MergeWithEnv(_activeProfile, _env);

        MainGitHubOwnerBox.Text = _activeProfile.GitHubOwner;
        MainGitHubTokenBox.Text = _profiles.GitHubToken(_activeProfile);
        GitHubAuthStatusLabel.Text = string.IsNullOrWhiteSpace(MainGitHubTokenBox.Text)
            ? "Not authenticated"
            : "Credential stored in system keyring · verification pending";
        MainGitRemoteBox.Text = _activeProfile.DefaultRemote;
        MainBranchBox.Text = _activeProfile.DefaultBranch;
        MainGitHubRepoBox.Text = _activeProfile.GitHubRepo;
        _firebase.CliPath = _activeProfile.FirebaseCliPath;

        _githubBranches.Clear();
        if (!string.IsNullOrWhiteSpace(_activeProfile.DefaultBranch))
            _githubBranches.Add(_activeProfile.DefaultBranch);
        MainBranchCombo.SelectedIndex = _githubBranches.Count > 0 ? 0 : -1;

        UpdateCiCdButtonStates();
        UpdateContextBanner();
    }

    private void SaveSourceTabToProfile()
    {
        _activeProfile.GitHubOwner = MainGitHubOwnerBox.Text?.Trim() ?? "";
        _activeProfile.GitHubRepo = EffectiveRepo;
        _activeProfile.DefaultRemote = string.IsNullOrWhiteSpace(MainGitRemoteBox.Text)
            ? "origin" : MainGitRemoteBox.Text!.Trim();
        _activeProfile.DefaultBranch = MainBranchBox.Text?.Trim() ?? "";
        _profiles.SaveProfile(_activeProfile);

        if (!string.IsNullOrEmpty(MainGitHubTokenBox.Text))
        {
            try { _profiles.SetGitHubToken(_activeProfile, MainGitHubTokenBox.Text!.Trim()); }
            catch (Exception ex) { _toasts.Error("Could not store GitHub token", Toaster.Friendly(ex)); }
        }
        UpdateContextBanner();
    }

    private string EffectiveRepo =>
        MainGitHubRepoCombo.SelectedItem is GitHubRepoDto dto
            ? dto.Name
            : (MainGitHubRepoBox.Text?.Trim() ?? "");

    #endregion

    #region Variants

    private void LoadVariants(string project)
    {
        _variants.Clear();
        foreach (var v in _settings.GetVariants(project)) _variants.Add(v);
        string prev = _settings.GetSelectedVariant(project);
        VariantCombo.SelectedItem = _variants.Contains(prev) ? prev : _variants.FirstOrDefault();
    }

    private void VariantCombo_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (!_uiReady || VariantCombo.SelectedItem is not string sel) return;
        _android.SelectedVariant = sel;
        if (!string.IsNullOrEmpty(_android.ProjectPath))
            _settings.SetSelectedVariant(_android.ProjectPath, sel);
        UpdateContextBanner();
    }

    private void DetectVariants_Click(object? s, RoutedEventArgs e)
    {
        if (!RequireProject()) return;
        Log("\n=== Detecting variants ===");
        try
        {
            string? prev = VariantCombo.SelectedItem as string;
            var detected = _flavors.DetectVariants(_android.ProjectPath);
            _variants.Clear();
            foreach (var v in detected) _variants.Add(v);
            _settings.SetVariants(_android.ProjectPath, detected);
            VariantCombo.SelectedItem = prev != null && _variants.Contains(prev)
                ? prev : _variants.FirstOrDefault();

            if (detected.Count == 0)
                _toasts.Warning("No variants detected",
                    "Falling back to Debug/Release. Check that app/build.gradle exists.");
            else
                _toasts.Success($"Detected {detected.Count} variant(s)", string.Join(", ", detected));
        }
        catch (Exception ex)
        {
            _toasts.Error("Variant detection failed", Toaster.Friendly(ex), details: ex.ToString());
        }
    }

    #endregion

    #region Devices

    private async Task RefreshDeviceList()
    {
        if (_deviceRefreshing) return;
        _deviceRefreshing = true;
        try
        {
            var devices = await _avd.GetDevicesSilent(CancellationToken.None);
            var online = devices.Where(d => d.IsOnline).ToList();
            var broken = devices.Where(d => !d.IsOnline).ToList();

            DeviceListLabel.Text = online.Count == 0
                ? (broken.Count > 0 ? $"(none online; {broken.Count} with problems)" : "(none)")
                : string.Join("   ", online.Select(d => d.DisplayName));
            DeviceListLabel.Foreground = online.Count == 0 && broken.Count > 0
                ? new SolidColorBrush(Color.Parse("#f9e2af"))
                : new SolidColorBrush(Color.Parse("#cdd6f4"));

            // Warn once per device about unauthorized/offline/permission states —
            // these are the ones a user can actually fix.
            foreach (var d in broken.Where(d => d.Problem != null))
                if (_warnedDevices.Add(d.Serial))
                    _toasts.Warning($"Device {d.Serial}: {d.State}", d.Problem);
            _warnedDevices.RemoveWhere(s => !devices.Any(d => d.Serial == s));

            string? prev = DeviceCombo.SelectedItem as string;
            _deviceSerials.Clear();
            _deviceSerials.Add(AllDevices);
            foreach (var d in online) _deviceSerials.Add(d.Serial);
            DeviceCombo.SelectedItem = prev != null && _deviceSerials.Contains(prev) ? prev : AllDevices;
        }
        catch (Exception ex)
        {
            // Polling failures must not spam; log only.
            Log($"[WARN] device scan: {ex.Message}");
        }
        finally { _deviceRefreshing = false; }
    }

    private readonly HashSet<string> _warnedDevices = new(StringComparer.OrdinalIgnoreCase);

    private void DeviceCombo_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is string sel)
            _android.TargetDevice = sel == AllDevices ? null : sel;
    }

    private void RefreshAuthorizedDevices()
    {
        _authorizedDevices.Clear();
        if (string.IsNullOrEmpty(_android.ProjectPath)) return;
        foreach (var serial in _settings.GetAuthorizedTestDevices(_android.ProjectPath))
            _authorizedDevices.Add(serial);
    }

    private void AuthorizeDevice_Click(object? s, RoutedEventArgs e)
    {
        if (!RequireProject()) return;
        string serial = AuthorizedSerialBox.Text?.Trim() ?? "";
        if (serial.Length == 0 && DeviceCombo.SelectedItem is string sel && sel != AllDevices)
            serial = sel;

        if (serial.Length == 0)
        {
            _toasts.Warning("No serial given",
                "Type a serial, or pick a specific device in the Target dropdown.");
            return;
        }

        if (_settings.AddAuthorizedTestDevice(_android.ProjectPath, serial))
            _toasts.Success("Device authorized for instrument tests", serial);
        else
            _toasts.Info("Already authorized", serial);

        AuthorizedSerialBox.Text = "";
        RefreshAuthorizedDevices();
    }

    private void RemoveAuthorizedDevice_Click(object? s, RoutedEventArgs e)
    {
        if (!RequireProject()) return;
        if (AuthorizedDevicesList.SelectedItem is not string serial)
        { _toasts.Info("Select a serial in the list first."); return; }

        if (_settings.RemoveAuthorizedTestDevice(_android.ProjectPath, serial))
            _toasts.Success("Device removed from authorized list", serial);
        RefreshAuthorizedDevices();
    }

    #endregion

    #region AVDs and environment

    private async Task LoadAvds()
    {
        try
        {
            var avds = await _avd.GetAvailableAvds(CancellationToken.None);
            _avds.Clear();
            foreach (var a in avds) _avds.Add(a);
            AvdCombo.SelectedItem = _settings.PreferredAvd is { } p && _avds.Contains(p)
                ? p : _avds.FirstOrDefault();
            if (avds.Length == 0)
                Log("[!] No AVDs found. Create one with avdmanager or Android Studio.");
        }
        catch (Exception ex) { Log($"[WARN] AVDs: {ex.Message}"); }
    }

    private void AvdCombo_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (!_uiReady || AvdCombo.SelectedItem is not string sel) return;
        _settings.PreferredAvd = sel;
        _settings.Save();
    }

    private async void RefreshAvds_Click(object? s, RoutedEventArgs e)
    {
        await LoadAvds();
        _toasts.Info($"{_avds.Count} AVD(s) found");
    }

    private async void Devices_Click(object? s, RoutedEventArgs e) =>
        await RunOp(async ct => (await _android.ListDevices(ct)).ExitCode, "List Devices", requireProject: false);

    private async void StartAvd_Click(object? s, RoutedEventArgs e)
    {
        if (!RequireIdle()) return;
        EnterBusy("Starting AVD");
        try
        {
            string? serial = await _avd.EnsureEmulatorRunning(_settings.PreferredAvd, _cts!.Token);
            if (serial != null)
            {
                await RefreshDeviceList();
                if (_deviceSerials.Contains(serial)) DeviceCombo.SelectedItem = serial;
                _toasts.Success("Emulator ready", serial);
            }
            else
            {
                _toasts.Error("Emulator did not start",
                    "See the log for details. Common causes: no AVD created, or /dev/kvm not accessible.");
            }
        }
        catch (OperationCanceledException) { _toasts.Warning("AVD start cancelled"); }
        catch (Exception ex) { _toasts.Error("AVD start failed", Toaster.Friendly(ex), ex.ToString()); }
        finally { ExitBusy(); }
    }

    private async void StopDaemons_Click(object? s, RoutedEventArgs e) =>
        await RunOp(_android.StopDaemons, "Stop Daemons", successMessage: "Gradle daemons stopped.");

    private async void RestartAdb_Click(object? s, RoutedEventArgs e) =>
        await RunOp(async ct =>
        {
            await _android.KillAdbServer(ct);
            return await _android.StartAdbServer(ct);
        }, "Restart ADB", requireProject: false, successMessage: "ADB server restarted.");

    private async void CheckEnv_Click(object? s, RoutedEventArgs e)
    {
        var problems = _android.Diagnose().ToList();
        var report = new StringBuilder();
        report.AppendLine($"SDK:         {_android.SdkPath}");
        report.AppendLine($"adb:         {_android.AdbPath}");
        report.AppendLine($"emulator:    {_android.EmulatorPath}");
        report.AppendLine($"git:         {Platform.Which("git") ?? "NOT FOUND"}");
        report.AppendLine($"java:        {_android.JavaEnvironment.Launcher?.JavaPath ?? "NOT FOUND"}");
        report.AppendLine($"JAVA_HOME:   {_android.JavaEnvironment.Launcher?.Home ?? "NOT FOUND"}");
        report.AppendLine($"keytool:     {(_android.JavaEnvironment.Launcher is { } jdk ? Path.Combine(jdk.Home, "bin", Platform.IsWindows ? "keytool.exe" : "keytool") : "NOT FOUND")}");
        report.AppendLine($"firebase:    {Platform.Which(_activeProfile.FirebaseCliPath) ?? "NOT FOUND"}");
        report.AppendLine($"gradlew:     {Platform.GradleWrapper(_android.ProjectPath) ?? "NOT FOUND"}");
        report.AppendLine($"/dev/kvm:    {(File.Exists("/dev/kvm") ? "present" : "MISSING")}");
        report.AppendLine($"secrets:     {SecretStore.BackendDescription}");
        report.AppendLine();
        report.AppendLine(_android.JavaEnvironment.Report());

        Log("\n=== Environment ===\n" + report);
        if (problems.Count == 0)
            await Dialogs.SuccessAsync(this, "Environment looks good", null, report.ToString());
        else
            await MessageDialog.ShowAsync(this, new($"{problems.Count} environment problem(s)",
                "These will cause failures in related operations:",
                Bullets: problems, Details: report.ToString(), Severity: ToastSeverity.Warning));
    }

    #endregion

    #region Build and lint

    private async void Clean_Click(object? s, RoutedEventArgs e) =>
        await RunOp(_android.CleanBuild, "Clean");

    private async void Build_Click(object? s, RoutedEventArgs e) =>
        await RunOp(_android.AssembleVariant, $"Build {_android.SelectedVariant}");

    private async void BuildAll_Click(object? s, RoutedEventArgs e) =>
        await RunOp(_android.AssembleAll, "Build All");

    private async void ListTasks_Click(object? s, RoutedEventArgs e) =>
        await RunOp(_android.ListTasks, "List Tasks");

    private async void Lint_Click(object? s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;
        string variant = _android.SelectedVariant;
        EnterBusy($"Lint {variant}");
        try
        {
            int code = await _android.RunLint(variant, _cts!.Token);
            var report = LintReportService.Parse(_android.ProjectPath, variant);
            Log($"===== Lint exit {code} =====");
            Log(report.Summary);

            if (report.Errors > 0)
                _toasts.Error($"Lint: {report.Errors} error(s)",
                    $"{report.Warnings} warning(s) also found.",
                    details: report.Detail,
                    actionLabel: report.HtmlPath != null ? "Open HTML report" : null,
                    action: report.HtmlPath != null ? () => Platform.OpenFile(report.HtmlPath) : null);
            else if (report.Warnings > 0)
                _toasts.Warning($"Lint clean, {report.Warnings} warning(s)",
                    "No errors.", details: report.Detail,
                    actionLabel: report.HtmlPath != null ? "Open HTML report" : null,
                    action: report.HtmlPath != null ? () => Platform.OpenFile(report.HtmlPath) : null);
            else if (code == 0)
                _toasts.Success("Lint clean", "No errors or warnings.");
            else
                _toasts.Error("Lint failed to run", $"Exit {code} with no report produced.");
        }
        catch (OperationCanceledException) { _toasts.Warning("Lint cancelled"); }
        catch (Exception ex) { _toasts.Error("Lint failed", Toaster.Friendly(ex), ex.ToString()); }
        finally { ExitBusy(); }
    }

    #endregion

    #region Tests

    private async void UnitTests_Click(object? s, RoutedEventArgs e) =>
        await RunTestsWithReport(_android.RunUnitTests, "UnitTests", false);

    private async void InstrumentTests_Click(object? s, RoutedEventArgs e) =>
        await RunTestsWithReport(_android.RunInstrumentTests, "InstrumentTests", true);

    private async Task RunTestsWithReport(
        Func<CancellationToken, Task<int>> testOp, string testType, bool ensureEmulator)
    {
        if (!RequireProject() || !RequireIdle()) return;

        EnterBusy(testType, totalSteps: 2);
        var buf = new StringBuilder();
        void capture(string l) => buf.AppendLine(l);
        _runner.OutputReceived += capture;
        bool started = false;

        try
        {
            if (ensureEmulator)
            {
                var target = await PrepareInstrumentTestTarget(_cts!.Token);
                if (target == null) return;   // toast already raised
            }

            Log($"\n===== {testType} ({_android.SelectedVariant}) =====");
            started = true;
            var startedAt = DateTime.Now;
            int code = await testOp(_cts!.Token);
            Log($"===== {testType} exit {code} =====");

            var rpt = _testReports.ParseResults(_android.ProjectPath, testType, startedAt);
            ShowSummary(rpt);
            ReportTestResults(testType, rpt);
            string path = _testReports.SaveReport(
                rpt, _android.ProjectPath, _android.SelectedVariant, buf.ToString());

            if (rpt.Total == 0)
            {
                Log($"[WARN] {testType} produced NO results this run.");
                _toasts.Warning($"{testType}: no tests ran",
                    code == 0
                        ? "Gradle succeeded but produced no results — the task may have been UP-TO-DATE, " +
                          "or the variant has no tests."
                        : $"Gradle exited {code} before tests executed.",
                    details: buf.Length > 4000 ? buf.ToString()[^4000..] : buf.ToString());
            }
            else if (rpt.Failed > 0)
            {
                _toasts.Error($"{testType}: {rpt.Failed} of {rpt.Total} failed",
                    $"{rpt.Passed} passed, {rpt.Skipped} skipped.",
                    details: BuildFailureDetails(rpt),
                    actionLabel: "Open report", action: () => Platform.OpenFile(path));
            }
            else
            {
                _toasts.Success($"{testType}: all {rpt.Total} passed",
                    $"{rpt.Skipped} skipped · {rpt.TotalTimeSeconds:F1}s",
                    actionLabel: "Open report", action: () => Platform.OpenFile(path));
            }
        }
        catch (OperationCanceledException)
        {
            Log($"===== {testType} cancelled =====");
            _toasts.Warning($"{testType} cancelled");
        }
        catch (Exception ex)
        {
            Log($"[ERROR] {ex.Message}");
            _toasts.Error($"{testType} failed", Toaster.Friendly(ex), ex.ToString());
        }
        finally
        {
            _runner.OutputReceived -= capture;
            if (started && _cts is { } operationCts)
                await StopGradleResourcesAfterTesting(
                    testType, operationCts.Token, advanceProgress: true);
            ExitBusy();
        }
    }

    private async Task StopGradleResourcesAfterTesting(
        string ctx, CancellationToken operationToken, PipelineLogger? logger = null,
        bool advanceProgress = false)
    {
        if (operationToken.IsCancellationRequested)
        {
            Log($"Gradle daemon cleanup skipped after {ctx}: the operation was aborted.");
            logger?.Line("Gradle daemon cleanup skipped because the operation was aborted.");
            return;
        }

        Log($"Stopping Gradle daemons after {ctx}…");
        if (advanceProgress)
            ReportOperationProgress(
                Math.Min(_operationCurrent + 1, _operationTotal), _operationTotal,
                "Stop Gradle daemons");
        else
            SetStatus("Stopping Gradle");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await _android.StopDaemons(timeout.Token);
            Log("Gradle daemons stopped.");
            logger?.Line("Gradle daemons stopped.");
            if (advanceProgress)
                ReportOperationProgress(_operationCurrent, _operationTotal,
                    "Stop Gradle daemons", completed: true, detail: "completed");
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            Log("Gradle daemon cleanup aborted.");
            logger?.Line("Gradle daemon cleanup aborted.");
        }
        catch (Exception ex)
        {
            Log($"[WARN] {ex.Message}");
            logger?.Line($"[WARN] {ex.Message}");
        }
    }

    // Chooses the device connected{Variant}AndroidTest runs on. Physical devices
    // are preferred — a large instrument suite is too heavy for emulators. Order:
    // the device explicitly selected in the combo wins, then any connected
    // authorized physical device, then an emulator as a last resort. NOTE that
    // connectedAndroidTest ends by UNINSTALLING the app under test, deleting ALL
    // of its data on the target. That is guarded two ways: when the project's
    // authorized list is non-empty only those serials may be targeted (emulators
    // are exempt — their data is disposable), and whenever the target is physical
    // an unmissable banner plus a toast is raised first.
    private async Task<string?> PrepareInstrumentTestTarget(CancellationToken ct)
    {
        var devices = await _avd.GetDevicesSilent(ct);
        var authorized = _settings.GetAuthorizedTestDevices(_android.ProjectPath);
        bool Authorized(DeviceInfo d) => authorized.Count == 0 ||
            authorized.Contains(d.Serial, StringComparer.OrdinalIgnoreCase);

        var selected = devices.FirstOrDefault(d => d.IsOnline &&
            string.Equals(d.Serial, _android.TargetDevice, StringComparison.OrdinalIgnoreCase));

        if (selected?.IsEmulator != true)
        {
            if (selected != null && !Authorized(selected))
            {
                Log($"[WARN] Selected device {selected.Serial} is NOT authorized for this project.");
                _toasts.Warning($"{selected.Serial} is not authorized",
                    "It will not be used for instrument tests. Add it under Test Devices to allow it.");
                selected = null;
            }

            var target = selected
                ?? devices.FirstOrDefault(d => d.IsOnline && !d.IsEmulator && Authorized(d));

            if (target != null)
            {
                _android.TargetDevice = target.Serial;
                await RefreshDeviceList();
                if (_deviceSerials.Contains(target.Serial)) DeviceCombo.SelectedItem = target.Serial;
                AnnounceInstrumentWipe(target.Serial);
                return target.Serial;
            }

            Log(devices.Any(d => d.IsOnline && !d.IsEmulator)
                ? "Connected phone(s) present but none authorized; falling back to an emulator."
                : "No physical device connected; falling back to an emulator.");
        }

        string? serial = await _avd.EnsureEmulatorReadyForTests(_settings.PreferredAvd, ct);
        if (serial == null)
        {
            Log("[ERROR] No device or emulator available.");
            _toasts.Error("No instrument test target",
                "No authorized physical device and no emulator could be started.",
                details: "Connect a device and authorize it under Test Devices, or create an AVD. " +
                         "If an AVD exists, check /dev/kvm access.");
            return null;
        }
        _android.TargetDevice = serial;
        await RefreshDeviceList();
        if (_deviceSerials.Contains(serial)) DeviceCombo.SelectedItem = serial;
        return serial;
    }

    private void AnnounceInstrumentWipe(string serial)
    {
        Log("");
        Log("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
        Log($"!!  INSTRUMENT TESTS -> PHYSICAL DEVICE {serial}");
        Log("!!  connectedAndroidTest UNINSTALLS the app when the run ends.");
        Log("!!  ALL app data on this device will be DELETED.");
        Log("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
        Log("");
        _toasts.Warning($"Instrument tests on physical device {serial}",
            "All app data on this device will be deleted when the run finishes.");
    }

    private void ShowSummary(TestReport r)
    {
        Log("\n---------- TEST SUMMARY ----------");
        Log($"  Total: {r.Total}  Passed: {r.Passed}  Failed: {r.Failed}  Skipped: {r.Skipped}");
        foreach (var f in r.Failures)
        {
            Log($"   [X] {f.ClassName}.{f.Name}");
            if (f.FailureMessage != null) Log($"       {f.FailureMessage}");
        }
        Log("----------------------------------");
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
        }
        return sb.ToString();
    }

    private async void FullTestRun_Click(object? s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;

        if (!await Dialogs.ConfirmAsync(this, "Full test run",
                "Clean → invalidate caches → unit tests → instrument tests.\n\n" +
                "Instrument tests uninstall the app on the target device, deleting its data.",
                yes: "Run", danger: true))
            return;

        string v = _android.SelectedVariant;
        var steps = new List<WorkflowStep>
        {
            new() { Name = "Stop Daemons", Action = _android.StopDaemons, StopOnFailure = false },
            new() { Name = "Clean", Action = _android.CleanBuild },
            new() { Name = "Invalidate Caches",
                    Action = async ct => { await _cache.CleanCaches(CacheCleanLevel.Medium, ct); return 0; } },
            new() { Name = $"Unit Tests ({v})", Action = _android.RunUnitTests, MaxRetries = 2 },
            new() { Name = "Select Instrument Target",
                    Action = async ct => await PrepareInstrumentTestTarget(ct) != null ? 0 : 1 },
            new() { Name = $"Instrumented Tests ({v})", Action = _android.RunInstrumentTests, MaxRetries = 2 }
        };
        EnterBusy("Full Test Run", steps.Count);

        try
        {
            var startedAt = DateTime.Now;
            bool ok = await _workflow.ExecuteAsync("Full Test Run", steps, _cts!.Token);

            var unit = _testReports.ParseResults(_android.ProjectPath, "UnitTests", startedAt);
            var inst = _testReports.ParseResults(_android.ProjectPath, "InstrumentTests", startedAt);
            if (unit.Total > 0) { ShowSummary(unit); _testReports.SaveReport(unit, _android.ProjectPath, v, "(workflow)"); }
            if (inst.Total > 0) { ShowSummary(inst); _testReports.SaveReport(inst, _android.ProjectPath, v, "(workflow)"); }

            await RefreshDeviceList();

            int failed = unit.Failed + inst.Failed;
            int total = unit.Total + inst.Total;
            if (ok && failed == 0 && total > 0)
                _toasts.Success($"Full test run passed — {total} tests", $"Unit {unit.Total} · Instrument {inst.Total}");
            else if (total == 0)
                _toasts.Warning("Full test run produced no results",
                    "A step failed before tests executed, or no tests exist for this variant.");
            else
                _toasts.Error($"Full test run: {failed} failure(s)",
                    $"Unit {unit.Failed}/{unit.Total} · Instrument {inst.Failed}/{inst.Total}",
                    details: BuildFailureDetails(unit) + "\n" + BuildFailureDetails(inst));
        }
        catch (OperationCanceledException) { _toasts.Warning("Full test run cancelled"); }
        catch (Exception ex) { _toasts.Error("Full test run failed", Toaster.Friendly(ex), ex.ToString()); }
        finally
        {
            if (_cts is { } operationCts)
                await StopGradleResourcesAfterTesting("full test run", operationCts.Token);
            ExitBusy();
        }
    }

    #endregion

    #region Cache

    private async void CacheLight_Click(object? s, RoutedEventArgs e) => await RunCacheClean(CacheCleanLevel.Light);
    private async void CacheMedium_Click(object? s, RoutedEventArgs e) => await RunCacheClean(CacheCleanLevel.Medium);

    private async void CacheHeavy_Click(object? s, RoutedEventArgs e)
    {
        if (!await Dialogs.ConfirmDestructiveAsync(this, "Heavy clean",
                $"This deletes the global Gradle cache at {Paths.GradleHome}/caches.\n\n" +
                "Your next build will re-download every dependency — this can take a long time " +
                "and requires a working network connection.",
                checkboxText: "I understand dependencies will be re-downloaded",
                yes: "Delete global cache"))
            return;
        await RunCacheClean(CacheCleanLevel.Heavy);
    }

    private async Task RunCacheClean(CacheCleanLevel level)
    {
        if (!RequireProject() || !RequireIdle()) return;
        EnterBusy($"Cache ({level})");
        try
        {
            await _cache.CleanCaches(level, _cts!.Token);
            _toasts.Success($"{level} cache clean complete");
        }
        catch (OperationCanceledException) { _toasts.Warning("Cache clean cancelled"); }
        catch (Exception ex) { _toasts.Error("Cache clean failed", Toaster.Friendly(ex), ex.ToString()); }
        finally { ExitBusy(); }
    }

    private async void CleanLocks_Click(object? s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;
        EnterBusy("Cleaning locks");
        try
        {
            await Task.Run(() => _cache.CleanLockFilesOnly());
            _toasts.Success("Lock file cleanup complete");
        }
        catch (Exception ex) { _toasts.Error("Lock cleanup failed", Toaster.Friendly(ex), ex.ToString()); }
        finally { ExitBusy(); }
    }

    #endregion

    #region Source control

    private async void LoadRepos_Click(object? s, RoutedEventArgs e)
    {
        string owner = MainGitHubOwnerBox.Text?.Trim() ?? "";
        string token = (MainGitHubTokenBox.Text ?? "").Trim();
        if (token.Length == 0)
        {
            _toasts.Warning("GitHub token required",
                "Enter a personal access token with 'repo' scope to list repositories.");
            return;
        }

        await RunTask(async ct =>
        {
            var repos = await _issueService.GetReposAsync(token, owner.Length == 0 ? null : owner, ct);
            _githubRepos.Clear();
            foreach (var r in repos) _githubRepos.Add(r);
            GitHubAuthStatusLabel.Text = $"✓ Authenticated · {repos.Count} accessible repositories";

            var match = repos.FirstOrDefault(r =>
                string.Equals(r.Name, _activeProfile.GitHubRepo, StringComparison.OrdinalIgnoreCase));
            if (match != null) MainGitHubRepoCombo.SelectedItem = match;

            if (repos.Count == 0)
                _toasts.Warning("No repositories returned",
                    "Check the owner name and that the token has 'repo' scope.");
            else
                _toasts.Success($"Loaded {repos.Count} repositories");
        }, "Load Repos");
    }

    private async void SaveCreds_Click(object? s, RoutedEventArgs e)
    {
        if (!RequireProject()) return;
        string token = (MainGitHubTokenBox.Text ?? "").Trim();
        if (token.Length == 0)
        {
            _toasts.Warning("GitHub token required", "Paste the output of `gh auth token` first.");
            return;
        }

        await RunTask(async ct =>
        {
            await VerifyAndStoreGitHubToken(token, ct);
        }, "Verify GitHub credentials");
    }

    private async void UseGhLogin_Click(object? s, RoutedEventArgs e)
    {
        if (!RequireProject()) return;
        await RunTask(async ct =>
        {
            string token = await _ghCredentials.ReadTokenAsync(ct);
            await VerifyAndStoreGitHubToken(token, ct);
        }, "Import GitHub CLI login");
    }

    private async Task VerifyAndStoreGitHubToken(string token, CancellationToken ct)
    {
        string login = await _issueService.GetAuthenticatedLoginAsync(token, ct);
        MainGitHubTokenBox.Text = token;
        if (string.IsNullOrWhiteSpace(MainGitHubOwnerBox.Text))
            MainGitHubOwnerBox.Text = login;
        SaveSourceTabToProfile();
        GitHubAuthStatusLabel.Text = $"✓ Authenticated as @{login} · stored in system keyring";
        _toasts.Success("GitHub credentials verified",
            $"Signed in as @{login}. Stored in {SecretStore.BackendDescription.ToLowerInvariant()}.");
    }

    private void LoadFromEnv_Click(object? s, RoutedEventArgs e)
    {
        try
        {
            _env.Reload();
            MainGitHubOwnerBox.Text = _env.GitHubOwner;
            MainGitHubRepoBox.Text = _env.GitHubRepo;
            if (!string.IsNullOrWhiteSpace(_env.GitHubToken))
                MainGitHubTokenBox.Text = _env.GitHubToken;
            _toasts.Success("Loaded from .env", Paths.EnvFile);
        }
        catch (Exception ex) { _toasts.Error("Could not read .env", Toaster.Friendly(ex), ex.ToString()); }
    }

    private async void SaveToEnv_Click(object? s, RoutedEventArgs e)
    {
        if (!await Dialogs.ConfirmAsync(this, "Write secrets to .env?",
                $"This writes tokens and passwords in PLAIN TEXT to:\n{Paths.EnvFile}\n\n" +
                "The keyring is the secure store; .env is only for bootstrapping another machine.",
                yes: "Write plain text", danger: true))
            return;
        try
        {
            _env.GitHubOwner = MainGitHubOwnerBox.Text?.Trim() ?? "";
            _env.GitHubRepo = EffectiveRepo;
            _env.GitHubToken = MainGitHubTokenBox.Text ?? "";
            _env.FirebaseAppId = _activeProfile.FirebaseAppId;
            _env.FirebaseCredentials = _activeProfile.FirebaseCredentialsJson;
            _env.FirebaseCliPath = _activeProfile.FirebaseCliPath;
            _env.KeystorePath = _activeProfile.KeystorePath;
            _env.KeyAlias = _activeProfile.KeyAlias;
            _env.StorePassword = _secrets.Get($"{_activeProfile.Key}/store-password");
            _env.KeyPassword = _secrets.Get($"{_activeProfile.Key}/key-password");
            _env.Save();
            _toasts.Warning("Secrets written in plain text", Paths.EnvFile);
        }
        catch (Exception ex) { _toasts.Error("Could not write .env", Toaster.Friendly(ex), ex.ToString()); }
    }

    private void RepoCombo_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (MainGitHubRepoCombo.SelectedItem is not GitHubRepoDto dto) return;
        MainGitHubRepoBox.Text = dto.Name;
        if (!string.IsNullOrWhiteSpace(dto.OwnerLogin))
            MainGitHubOwnerBox.Text = dto.OwnerLogin;
    }

    private async void MainLoadBranches_Click(object? s, RoutedEventArgs e)
    {
        if (!RequireIdle()) return;
        SaveSourceTabToProfile();

        string owner = _activeProfile.GitHubOwner;
        string repo = _activeProfile.GitHubRepo;
        if (owner.Length == 0 || repo.Length == 0)
        {
            _toasts.Warning("Repository not set", "Enter an owner and select or type a repository.");
            return;
        }

        await RunTask(async ct =>
        {
            var branches = await _github.GetBranchesAsync(
                owner, repo, _profiles.GitHubToken(_activeProfile), ct);
            _githubBranches.Clear();
            foreach (var b in branches) _githubBranches.Add(b);

            string sel = _activeProfile.DefaultBranch;
            MainBranchCombo.SelectedItem =
                branches.FirstOrDefault(b => string.Equals(b, sel, StringComparison.OrdinalIgnoreCase))
                ?? branches.FirstOrDefault();

            if (branches.Count == 0)
                _toasts.Warning("No branches found", $"{owner}/{repo} returned no branches.");
            else
                _toasts.Success($"Loaded {branches.Count} branches", $"{owner}/{repo}");
        }, "Load Branches");
    }

    private void MainBranchCombo_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (!_uiReady || MainBranchCombo.SelectedItem is not string b) return;
        MainBranchBox.Text = b;
        _activeProfile.DefaultBranch = b;
        try { _profiles.SaveProfile(_activeProfile); }
        catch (Exception ex) { _toasts.Error("Could not save profile", Toaster.Friendly(ex)); }
        UpdateContextBanner();
    }

    private async void MainPullBranch_Click(object? s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;
        SaveSourceTabToProfile();

        string remote = _activeProfile.DefaultRemote;
        string branch = _activeProfile.DefaultBranch;
        if (!GitNames.IsValid(remote) || !GitNames.IsValid(branch))
        {
            _toasts.Warning("Invalid remote or branch", "Check the Remote and Name fields.");
            return;
        }

        EnterBusy("Pulling branch");
        try
        {
            Log($"\n===== Pull {remote}/{branch} into LOCAL project =====");
            int code = await _git.UpdateToRemoteBranch(_android.ProjectPath, remote, branch, _cts!.Token);
            if (code == 0)
            {
                string sha = await _git.ShortSha(_android.ProjectPath, "HEAD", _cts.Token);
                _toasts.Success($"Pulled {remote}/{branch}",
                    $"Local project is now at {sha}. Builds and tests will use this code.");
            }
            else
            {
                _toasts.Error($"Could not pull {remote}/{branch}",
                    "See the log — the working tree may be dirty, or the branch has diverged.");
            }
        }
        catch (OperationCanceledException) { _toasts.Warning("Pull cancelled"); }
        catch (Exception ex) { _toasts.Error("Pull failed", Toaster.Friendly(ex), ex.ToString()); }
        finally { ExitBusy(); }
    }

    private async void RepoStatus_Click(object? s, RoutedEventArgs e)
    {
        if (!RequireProject()) return;
        try
        {
            var ct = CancellationToken.None;
            if (!await _git.IsRepo(_android.ProjectPath, ct))
            {
                _toasts.Warning("Not a git repository", _android.ProjectPath);
                return;
            }
            string branch = await _git.CurrentBranch(_android.ProjectPath, ct);
            string sha = await _git.ShortSha(_android.ProjectPath, "HEAD", ct);
            bool dirty = await _git.IsDirty(_android.ProjectPath, ct);
            string status = await _git.StatusSummary(_android.ProjectPath, ct);

            Log($"\n=== Repo status ===\n{status}");
            if (dirty)
                _toasts.Warning($"{branch} @ {sha} — uncommitted changes",
                    "CI and branch switching will refuse to run until this is clean.", details: status);
            else
                _toasts.Success($"{branch} @ {sha} — clean", details: status);
        }
        catch (Exception ex) { _toasts.Error("Could not read repo status", Toaster.Friendly(ex), ex.ToString()); }
    }

    #endregion

    #region Issues

    private string IssueOwner => _activeProfile.GitHubOwner;
    private string IssueRepo => _activeProfile.GitHubRepo;
    private string IssueToken => _profiles.GitHubToken(_activeProfile);

    private async void PullIssues_Click(object? s, RoutedEventArgs e)
    {
        SaveSourceTabToProfile();
        if (IssueOwner.Length == 0 || IssueRepo.Length == 0)
        {
            _toasts.Warning("Repository not set", "Set owner and repository on the Source tab first.");
            return;
        }
        string state = (IssueStateCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "open";

        await RunTask(async ct =>
        {
            var issues = await _issueService.GetIssuesAsync(IssueOwner, IssueRepo, IssueToken, state, ct);
            _githubIssues.Clear();
            foreach (var i in issues) _githubIssues.Add(i);
            IssueCountLabel.Text = $"{issues.Count} issues";
            _toasts.Success($"Loaded {issues.Count} {state} issue(s)", $"{IssueOwner}/{IssueRepo}");
        }, "Pull Issues");
    }

    private async void IssuesGrid_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (IssuesGrid.SelectedItem is not GitHubIssueModel issue) return;

        IssueDetailHeader.Text = $"#{issue.Number}: {issue.Title}";
        IssueDetailMeta.Text = $"State: {issue.State}  ·  Labels: {issue.LabelsSummary}  ·  " +
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
        catch (Exception ex)
        {
            IssueCommentHeader.Text = "Comments (failed to load)";
            _toasts.Warning("Could not load comments", Toaster.Friendly(ex));
        }
    }

    private async void AddComment_Click(object? s, RoutedEventArgs e)
    {
        if (IssuesGrid.SelectedItem is not GitHubIssueModel issue)
        { _toasts.Info("Select an issue first"); return; }
        string body = NewCommentBox.Text?.Trim() ?? "";
        if (body.Length == 0) { _toasts.Warning("Comment is empty"); return; }

        try
        {
            if (await _issueService.AddCommentAsync(IssueOwner, IssueRepo, IssueToken, issue.Number, body))
            {
                NewCommentBox.Text = "";
                _toasts.Success($"Comment added to #{issue.Number}");
                IssuesGrid_SelectionChanged(s, null!);
            }
            else _toasts.Error("Comment was rejected by GitHub", "Check the token's permissions.");
        }
        catch (Exception ex) { _toasts.Error("Could not add comment", Toaster.Friendly(ex), ex.ToString()); }
    }

    private async void CloseIssue_Click(object? s, RoutedEventArgs e)
    {
        if (IssuesGrid.SelectedItem is not GitHubIssueModel issue)
        { _toasts.Info("Select an issue first"); return; }
        if (!await Dialogs.ConfirmAsync(this, "Close issue?", $"#{issue.Number}: {issue.Title}")) return;

        try
        {
            if (await _issueService.UpdateStateAsync(IssueOwner, IssueRepo, IssueToken, issue.Number, "closed"))
            { issue.State = "closed"; _toasts.Success($"Closed #{issue.Number}"); }
            else _toasts.Error($"Could not close #{issue.Number}", "Check the token's permissions.");
        }
        catch (Exception ex) { _toasts.Error("Close failed", Toaster.Friendly(ex), ex.ToString()); }
    }

    private async void ReopenIssue_Click(object? s, RoutedEventArgs e)
    {
        if (IssuesGrid.SelectedItem is not GitHubIssueModel issue)
        { _toasts.Info("Select an issue first"); return; }
        try
        {
            if (await _issueService.UpdateStateAsync(IssueOwner, IssueRepo, IssueToken, issue.Number, "open"))
            { issue.State = "open"; _toasts.Success($"Reopened #{issue.Number}"); }
            else _toasts.Error($"Could not reopen #{issue.Number}");
        }
        catch (Exception ex) { _toasts.Error("Reopen failed", Toaster.Friendly(ex), ex.ToString()); }
    }

    #endregion

    #region Reports

    private void RefreshReportsTab()
    {
        ReportsGrid.ItemsSource = _deploymentReports.All;
        var (total, ok, fail) = _deploymentReports.Stats();
        double pct = total > 0 ? ok * 100.0 / total : 0;
        var last = _deploymentReports.All.FirstOrDefault();
        ReportsSummaryLabel.Text =
            $"Deployments: {total}   ·   Passed: {ok} ({pct:F0}%)   ·   Failed: {fail}   ·   " +
            $"Last: {last?.TimestampDisplay ?? "—"}";
    }

    private void ReportsGrid_SelectionChanged(object? s, SelectionChangedEventArgs e) =>
        FailuresGrid.ItemsSource = (ReportsGrid.SelectedItem as DeploymentRecord)?.Failures;

    private void RefreshReports_Click(object? s, RoutedEventArgs e)
    {
        RefreshReportsTab();
        _toasts.Info($"{_deploymentReports.All.Count} deployment record(s)");
    }

    private void OpenSelectedLog_Click(object? s, RoutedEventArgs e)
    {
        if (ReportsGrid.SelectedItem is not DeploymentRecord r)
        { _toasts.Info("Select a report first"); return; }
        if (string.IsNullOrWhiteSpace(r.LogPath) || !File.Exists(r.LogPath))
        { _toasts.Warning("Log file missing", r.LogPath ?? "(no path recorded)"); return; }

        try { Platform.RevealFile(r.LogPath); }
        catch (Exception ex) { _toasts.Error("Could not open log", Toaster.Friendly(ex)); }
    }

    private void OpenReports_Click(object? s, RoutedEventArgs e) => OpenDir(Paths.TestReports);
    private void OpenLogs_Click(object? s, RoutedEventArgs e) => OpenDir(Paths.PipelineLogs);

    private void OpenDir(string dir)
    {
        try { Platform.OpenDirectory(dir); }
        catch (Exception ex)
        {
            _toasts.Error("Could not open folder", Toaster.Friendly(ex),
                details: $"{dir}\n\nIs xdg-utils installed?");
        }
    }

    private void RecordDeployment(PipelineRunResult result, TestReport? unit = null, TestReport? instr = null)
    {
        try
        {
            var record = _deploymentReports.CreateRecord(
                ProjectName(), _android.SelectedVariant, _activeProfile.DefaultBranch,
                result.Success, result.Status, result.LogPath, unit, instr);
            _deploymentReports.Add(record);
            Dispatcher.UIThread.Post(RefreshReportsTab);
        }
        catch (Exception ex) { Log($"[WARN] could not record deployment: {ex.Message}"); }
    }

    private string ProjectName() => Path.GetFileName(_android.ProjectPath.TrimEnd('/', '\\'));

    #endregion

    #region CI / CD

    private void UpdateCiCdButtonStates()
    {
        bool fb = _activeProfile.HasFirebase;
        bool signing = _profiles.HasCompleteSigning(_activeProfile);

        DeployFirebaseButton.IsEnabled = fb;
        ToolTip.SetTip(DeployFirebaseButton,
            fb ? "Upload an APK to Firebase App Distribution" : "Complete Firebase Config first.");

        ToolTip.SetTip(BuildSignedButton,
            signing ? "Sign the local project APK with this project's keystore"
                    : "Keystore and passwords not set — the dialog will prompt for them.");

        FullPipelineButton.IsEnabled = true;
        ToolTip.SetTip(FullPipelineButton,
            fb && signing ? "Test → build signed → deploy"
                          : "Needs signing and Firebase config (the dialog will prompt).");
    }

    private async void FirebaseConfig_Click(object? s, RoutedEventArgs e)
    {
        if (!RequireProject()) return;
        using var gate = await _configGate.AcquireAsync("Firebase Config dialog");

        var result = await new FirebaseConfigWindow(FirebaseConfigDraft.From(_activeProfile))
            .ShowDialog<FirebaseConfigDraft?>(this);
        if (result is null) { _toasts.Info("Firebase config unchanged"); return; }

        try
        {
            result.ApplyTo(_activeProfile);
            _profiles.SaveProfile(_activeProfile);
            _firebase.CliPath = _activeProfile.FirebaseCliPath;
            UpdateCiCdButtonStates();
            UpdateContextBanner();
            _toasts.Success("Firebase configured", $"App {result.AppId}");
        }
        catch (Exception ex)
        {
            _toasts.Error("Could not save Firebase config", Toaster.Friendly(ex), ex.ToString());
        }
    }

    private async void BuildSigned_Click(object? s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;
        using var gate = await _configGate.AcquireAsync("Build Signed APK dialog");

        var draft = SigningDraft.From(_activeProfile, _secrets, _settings.CiCd);
        var result = await new BuildSignedApkWindow(draft, _variants, _android.SelectedVariant,
                _android.JavaEnvironment.Launcher?.Home)
            .ShowDialog<SigningDraft?>(this);
        if (result is null) { _toasts.Info("Build cancelled"); return; }

        try
        {
            result.ApplyTo(_activeProfile, _secrets, _settings.CiCd);
            _profiles.SaveProfile(_activeProfile);
            _settings.Save();
            UpdateCiCdButtonStates();
            UpdateContextBanner();
        }
        catch (Exception ex)
        {
            _toasts.Error("Could not save signing credentials", Toaster.Friendly(ex), ex.ToString());
            return;
        }

        var signing = new SigningSpec(result.KeystorePath, result.KeyAlias,
                                      result.StorePassword, result.KeyPassword);
        var logger = new PipelineLogger("BSA", ProjectName());
        _pipelineLogger = logger;
        EnterBusy("Build Signed APK");
        try
        {
            int code = await _android.BuildSignedApk(signing, result.SelectedVariants, _cts!.Token);
            Log($"Build exit {code}");

            if (code != 0)
            {
                _toasts.Error("Signed build failed", $"Gradle exited {code}.",
                    actionLabel: "Open log", action: () => Platform.OpenFile(logger.FilePath));
                return;
            }

            var found = new List<string>();
            var missing = new List<string>();
            foreach (var v in result.SelectedVariants)
            {
                var apk = FindApk(v);
                if (apk != null) { found.Add($"{v}: {apk}"); Log($"APK ({v}): {apk}"); }
                else { missing.Add(v); Log($"[WARN] APK for variant '{v}' not found."); }
            }

            if (missing.Count == 0)
                _toasts.Success($"Signed {found.Count} APK(s)", string.Join('\n', found.Select(Path.GetFileName)),
                    details: string.Join('\n', found),
                    actionLabel: "Show in folder",
                    action: () => Platform.RevealFile(found[0].Split(": ", 2)[1]));
            else
                _toasts.Warning($"Built, but {missing.Count} APK(s) not found",
                    $"Missing: {string.Join(", ", missing)}",
                    details: string.Join('\n', found));
        }
        catch (OperationCanceledException) { _toasts.Warning("Signed build cancelled"); }
        catch (Exception ex) { _toasts.Error("Signed build failed", Toaster.Friendly(ex), ex.ToString()); }
        finally { logger.Save(); _pipelineLogger = null; ExitBusy(); }
    }

    private async void DeployFirebase_Click(object? s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;
        using var gate = await _configGate.AcquireAsync("Deploy to Firebase dialog");

        if (!_activeProfile.HasFirebase)
        {
            _toasts.Warning("Firebase not configured", "Open Firebase Config first.");
            return;
        }

        string deployVariant = _settings.CiCd.BuildVariants.FirstOrDefault() ?? _android.SelectedVariant;
        var draft = DeployDraft.From(_settings.CiCd, FindApk(deployVariant));
        var result = await new DeployFirebaseWindow(draft).ShowDialog<DeployDraft?>(this);
        if (result is null) { _toasts.Info("Deploy cancelled"); return; }

        result.ApplyTo(_settings.CiCd);
        _settings.Save();

        var fb = new FirebaseSpec(
            _activeProfile.FirebaseCliPath, _activeProfile.FirebaseAppId,
            _activeProfile.FirebaseCredentialsJson, "",
            result.TesterGroups, result.TesterEmails, result.ReleaseNotes);

        var logger = new PipelineLogger("DEPLOY", ProjectName());
        _pipelineLogger = logger;
        EnterBusy("Deploying");
        try
        {
            _firebase.CliPath = fb.CliPath;
            var r = await _firebase.Distribute(result.ApkPath, fb, _cts!.Token);

            if (r.ExitCode == 0)
                _toasts.Success("Uploaded to Firebase",
                    $"{Path.GetFileName(result.ApkPath)} → {fb.TesterGroups}",
                    actionLabel: r.ReleaseUrl != null ? "Open in console" : null,
                    action: r.ReleaseUrl != null ? () => Platform.OpenFile(r.ReleaseUrl) : null);
            else
                _toasts.Error("Firebase upload failed", $"CLI exited {r.ExitCode}.", details: r.Tail,
                    actionLabel: "Open log", action: () => Platform.OpenFile(logger.FilePath));

            RecordDeployment(new PipelineRunResult(r.ExitCode == 0,
                r.ExitCode == 0 ? "Deployed" : $"Deploy exit {r.ExitCode}", logger.FilePath));
        }
        catch (OperationCanceledException) { _toasts.Warning("Deploy cancelled"); }
        catch (Exception ex) { _toasts.Error("Deploy failed", Toaster.Friendly(ex), ex.ToString()); }
        finally { logger.Save(); _pipelineLogger = null; ExitBusy(); }
    }

    private async void FullPipeline_Click(object? s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;
        using var gate = await _configGate.AcquireAsync("Full Pipeline dialog");

        var cfg = _settings.CiCd;
        var model = new PipelineDialogModel(
            SigningDraft.From(_activeProfile, _secrets, cfg),
            FirebaseConfigDraft.From(_activeProfile),
            new DeployTargets(cfg.TesterGroups, cfg.TesterEmails, cfg.ReleaseNotes),
            new GitSourceDraft(cfg.SyncBranchBeforePipeline, cfg.PipelineRemoteName, cfg.PipelineBranch,
                _activeProfile.GitHubOwner, _activeProfile.GitHubRepo, _profiles.GitHubToken(_activeProfile)),
            _variants.ToList(), cfg.RunUnitTests, cfg.RunInstrumentTests, cfg.FailureAction);

        var result = await new FullPipelineWindow(model).ShowDialog<PipelineDialogResult?>(this);
        if (result is null) { _toasts.Info("Pipeline cancelled — no changes saved."); return; }

        try
        {
            result.ApplyTo(_activeProfile, cfg, _secrets);
            _profiles.SaveProfile(_activeProfile);
            _settings.Save();
            UpdateCiCdButtonStates();
            UpdateContextBanner();
        }
        catch (Exception ex)
        {
            _toasts.Error("Could not save pipeline config", Toaster.Friendly(ex), ex.ToString());
            return;
        }

        var request = BuildRequest(result);
        if (request.Validate() is { Count: > 0 } errors)
        {
            await Dialogs.ErrorAsync(this, "Cannot start pipeline", errors);
            return;
        }

        await RunFullPipeline(request);
    }

    /// <summary>Snapshot point — immutable from here on.</summary>
    private PipelineRequest BuildRequest(PipelineDialogResult r) => new(
        ProjectPath: _android.ProjectPath,
        ActiveVariant: _android.SelectedVariant,
        BuildVariants: r.Signing.SelectedVariants,
        Signing: new SigningSpec(r.Signing.KeystorePath, r.Signing.KeyAlias,
                                 r.Signing.StorePassword, r.Signing.KeyPassword),
        Firebase: new FirebaseSpec(r.Firebase.CliPath, r.Firebase.AppId,
                                   r.Firebase.CredentialsJsonPath, "",
                                   r.Deploy.Groups, r.Deploy.Emails, r.Deploy.Notes),
        Source: new GitSourceSpec(r.Source.SyncBeforeRun, r.Source.Remote, r.Source.Branch,
                                  r.Source.Owner, r.Source.Repository, r.Source.Token),
        RunUnitTests: r.RunUnitTests,
        RunInstrumentTests: r.RunInstrumentTests,
        FailureAction: r.FailureAction);

    private sealed record PipelineRunResult(bool Success, string Status, string LogPath);

    private async Task<PipelineRunResult> RunFullPipeline(
        PipelineRequest req, string statusText = "Full Pipeline")
    {
        var logger = new PipelineLogger("FP", ProjectName());
        _pipelineLogger = logger;
        var phases = new List<string>();
        if (req.Source.SyncBeforeRun) phases.Add("Sync source");
        phases.Add("Stop Gradle daemons");
        phases.Add("Clean");
        if (req.RunUnitTests) phases.Add("Unit tests");
        if (req.RunInstrumentTests)
        {
            phases.Add("Select instrument target");
            phases.Add("Instrument tests");
        }
        if (req.RunUnitTests || req.RunInstrumentTests)
            phases.Add("Stop test Gradle daemons");
        phases.Add("Build signed APK");
        phases.Add("Deploy to Firebase");

        EnterBusy(statusText, phases.Count);
        var ct = _cts!.Token;
        int phaseIndex = 0;
        void Phase(string name) =>
            ReportOperationProgress(++phaseIndex, phases.Count, name);
        void PhaseComplete(string name, string? detail = null) =>
            ReportOperationProgress(phaseIndex, phases.Count, name, completed: true, detail: detail);
        bool testsStarted = false, cleanupDone = false;
        TestReport? unitRpt = null, instrRpt = null;

        try
        {
            if (req.Source.SyncBeforeRun)
            {
                Phase("Sync source");
                logger.Section("SYNC SOURCE");
                logger.Line($"Remote: {req.Source.Remote}  Branch: {req.Source.Branch}");
                if (await _git.UpdateToRemoteBranch(
                        req.ProjectPath, req.Source.Remote, req.Source.Branch, ct) != 0)
                    return Fin(false, "Source update failed");
            }

            Phase("Stop Gradle daemons");
            logger.Section("STOP DAEMONS");
            await _android.StopDaemons(ct);

            Phase("Clean");
            logger.Section("CLEAN");
            if (await _android.CleanBuild(ct) != 0) return Fin(false, "Clean failed");

            if (req.RunUnitTests)
            {
                Phase("Unit tests");
                testsStarted = true;
                logger.Section("UNIT TESTS");
                var at = DateTime.Now;
                int c = await _android.RunUnitTests(ct);
                unitRpt = _testReports.ParseResults(req.ProjectPath, "UnitTests", at);
                if (unitRpt.Total == 0) Log("[WARN] Unit tests produced NO results this run.");
                ShowSummary(unitRpt);
                PhaseComplete("Unit tests",
                    unitRpt.Total > 0 ? $"{unitRpt.Passed} of {unitRpt.Total} passed" : "no test results");
                _testReports.SaveReport(unitRpt, req.ProjectPath, req.ActiveVariant, "(FP)");
                if ((unitRpt.Failed > 0 || c != 0) &&
                    !await HandleTestFailure("Unit Tests", unitRpt, req.FailureAction, logger))
                    return Fin(false, "Stopped after unit test failure");
            }
            else Log("Unit tests: SKIPPED (disabled in the pipeline dialog).");

            if (req.RunInstrumentTests)
            {
                testsStarted = true;
                Phase("Select instrument target");
                logger.Section("SELECT INSTRUMENT TARGET");
                if (await PrepareInstrumentTestTarget(ct) == null)
                    return Fin(false, "No instrument test target");

                Phase("Instrument tests");
                logger.Section("INSTRUMENT TESTS");
                var at = DateTime.Now;
                int c = await _android.RunInstrumentTests(ct);
                instrRpt = _testReports.ParseResults(req.ProjectPath, "InstrumentTests", at);
                if (instrRpt.Total == 0) Log("[WARN] Instrument tests produced NO results this run.");
                ShowSummary(instrRpt);
                PhaseComplete("Instrument tests",
                    instrRpt.Total > 0 ? $"{instrRpt.Passed} of {instrRpt.Total} passed" : "no test results");
                _testReports.SaveReport(instrRpt, req.ProjectPath, req.ActiveVariant, "(FP)");
                if ((instrRpt.Failed > 0 || c != 0) &&
                    !await HandleTestFailure("Instrument Tests", instrRpt, req.FailureAction, logger))
                    return Fin(false, "Stopped after instrument test failure");
            }
            else Log("Instrument tests: SKIPPED (disabled in the pipeline dialog).");

            if (testsStarted)
            {
                Phase("Stop test Gradle daemons");
                await StopGradleResourcesAfterTesting("pipeline tests", ct, logger);
                cleanupDone = true;
                ct.ThrowIfCancellationRequested();
            }

            Phase("Build signed APK");
            logger.Section("BUILD SIGNED APK");
            if (await _android.BuildSignedApk(req.Signing, req.BuildVariants, ct) != 0)
                return Fin(false, "Build failed");

            string deployVariant = req.DeployVariant;
            if (req.BuildVariants.Length > 1)
                Log($"Built: {string.Join(", ", req.BuildVariants)}. Deploying: {deployVariant}");

            var apk = FindApk(deployVariant);
            if (apk == null) return Fin(false, $"APK for variant '{deployVariant}' not found after build");
            Log($"APK ({deployVariant}): {apk}");

            Phase("Deploy to Firebase");
            logger.Section("DEPLOY TO FIREBASE");
            _firebase.CliPath = req.Firebase.CliPath;
            var dr = await _firebase.Distribute(apk, req.Firebase, ct);
            bool ok = dr.ExitCode == 0;
            PhaseComplete("Deploy to Firebase", ok ? "completed" : $"exit {dr.ExitCode}");
            Log(ok ? "\nPIPELINE COMPLETE" : $"\nDeploy returned {dr.ExitCode}");

            if (ok)
                _toasts.Success("Pipeline complete", $"{deployVariant} deployed to Firebase.",
                    actionLabel: dr.ReleaseUrl != null ? "Open in console" : "Open log",
                    action: () => Platform.OpenFile(dr.ReleaseUrl ?? logger.FilePath));
            else
                _toasts.Error("Deploy step failed", $"CLI exited {dr.ExitCode}.", details: dr.Tail,
                    actionLabel: "Open log", action: () => Platform.OpenFile(logger.FilePath));

            return Fin(ok, ok ? "Deployed successfully" : $"Deploy exit {dr.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            Log("\nPipeline cancelled.");
            _toasts.Warning("Pipeline cancelled");
            return Fin(false, "Cancelled");
        }
        catch (Exception ex)
        {
            Log($"\n[ERROR] {ex.Message}");
            _toasts.Error("Pipeline failed", Toaster.Friendly(ex), ex.ToString(),
                actionLabel: "Open log", action: () => Platform.OpenFile(logger.FilePath));
            return Fin(false, ex.Message);
        }
        finally
        {
            if (testsStarted && !cleanupDone)
                await StopGradleResourcesAfterTesting("pipeline tests", ct, logger);
            await RefreshDeviceList();
            logger.Save();
            Log($"Pipeline log: {logger.FilePath}");
            _pipelineLogger = null;
            ExitBusy();
        }

        PipelineRunResult Fin(bool success, string status)
        {
            var r = new PipelineRunResult(success, status, logger.FilePath);
            RecordDeployment(r, unitRpt, instrRpt);
            if (!success && status != "Cancelled")
                _toasts.Error($"Pipeline stopped: {status}", message: null,
                    actionLabel: "Open log", action: () => Platform.OpenFile(logger.FilePath));
            return r;
        }
    }

    private async Task<bool> HandleTestFailure(
        string stage, TestReport report, TestFailureAction action, PipelineLogger logger)
    {
        string det = BuildFailureDetails(report);
        logger.Line($"[{stage}] {report.Failed}/{report.Total} failed.\n{det}");

        switch (action)
        {
            case TestFailureAction.SilentFailure:
                Log($"[{stage}] failures ignored (silent mode).");
                _toasts.Warning($"{stage}: {report.Failed} failure(s) ignored",
                    "Silent mode is enabled — the pipeline is continuing.", details: det);
                return true;

            case TestFailureAction.AutomaticBounceBack:
                _toasts.Error($"{stage} failed — pipeline stopped",
                    $"{report.Failed} of {report.Total} tests failed.", details: det);
                return false;

            default:
                var dlg = new TestFailureDialog($"{stage} Failed", det,
                    action == TestFailureAction.WarningWithConfirm, report.Failed, report.Total);
                bool go = await dlg.ShowDialog<bool?>(this) == true;
                Log($"[{stage}] user chose to {(go ? "continue" : "stop")}.");
                return go;
        }
    }

    // Resolves the APK for one specific variant. Gradle writes APKs to
    // outputs/apk/<flavor>/<buildType>/ (or outputs/apk/<buildType>/ with no
    // flavors), and the concatenated directory segments equal the variant name
    // case-insensitively ("dev"+"qa" == "DevQa"). Matching on the path instead of
    // newest-file-anywhere guarantees a multi-variant build can never hand a
    // differently-signed sibling APK to the deploy step — installing that on a
    // tester device forces an uninstall and wipes app data.
    private string? FindApk(string variant)
    {
        if (string.IsNullOrWhiteSpace(_android.ProjectPath) || string.IsNullOrWhiteSpace(variant))
            return null;
        var root = Path.Combine(_android.ProjectPath, "app", "build", "outputs", "apk");
        if (!Directory.Exists(root)) return null;
        try
        {
            return Directory.GetFiles(root, "*.apk", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).Contains("unsigned", StringComparison.OrdinalIgnoreCase))
                .Where(f => ApkVariantOf(root, f).Equals(variant.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
        }
        catch (Exception ex) { Log($"[WARN] APK scan failed: {ex.Message}"); return null; }
    }

    private static string ApkVariantOf(string root, string apkPath)
    {
        var dir = Path.GetDirectoryName(Path.GetRelativePath(root, apkPath)) ?? "";
        return string.Concat(dir.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries));
    }

    #endregion

    #region Promotion CI/CD

    private async void PromotionConfig_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireProject()) return;
        CiPipelineDialogResult? result;
        using (await _configGate.AcquireAsync("Promotion CI/CD dialog"))
        {
            result = await new CiPipelineWindow(_settings.Pipeline)
                .ShowDialog<CiPipelineDialogResult?>(this);
            if (result is null)
            {
                _toasts.Info("Promotion settings unchanged");
                return;
            }

            _settings.Pipeline = result.Config;
            _settings.Save();
            _toasts.Success("Promotion settings saved",
                $"{result.Config.StagingBranch} → {result.Config.MainBranch}");
        }

        if (result.Action == CiPipelineDialogAction.DryRun)
            await RunPromotionCi(dryRun: true, triggeredBySchedule: false);
        else if (result.Action == CiPipelineDialogAction.Promote)
            await RunPromotionCi(dryRun: false, triggeredBySchedule: false);
    }

    private async void PromotionDryRun_Click(object? sender, RoutedEventArgs e) =>
        await RunPromotionCi(dryRun: true, triggeredBySchedule: false);

    private async void PromotionRun_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;
        var cfg = _settings.Pipeline;
        if (!await Dialogs.ConfirmAsync(this, "Promote staging to production?",
                $"This will run every enabled gate, merge {cfg.StagingBranch} into {cfg.MainBranch}, " +
                $"{(cfg.PushMain ? "push the result" : "leave the result unpushed")}, and " +
                $"{(cfg.BackMergeToDev ? $"back-merge to {cfg.DevBranch}." : "skip the back-merge.")}",
                yes: "Run promotion"))
            return;
        await RunPromotionCi(dryRun: false, triggeredBySchedule: false);
    }

    private async Task RunPromotionCi(bool dryRun, bool triggeredBySchedule)
    {
        if (!RequireProject() || !RequireIdle()) return;

        using var gate = triggeredBySchedule
            ? _configGate.TryAcquire("Scheduled promotion CI")
            : await _configGate.AcquireAsync("Promotion CI run");
        if (gate is null)
        {
            Log($"[schedule] promotion skipped — config is being edited ({_configGate.HeldBy}).");
            _toasts.Warning("Scheduled promotion skipped", "Promotion settings were being edited.");
            return;
        }

        var cfg = _settings.Pipeline;
        AndroidCommandCenter.Ci.FirestoreSink? sink = cfg.PublishToFirestore
            ? new AndroidCommandCenter.Ci.FirestoreSink(
                cfg.FirestoreProjectId, cfg.FirestoreCredentialsJson, cfg.FirestoreCollection)
            : null;
        var ci = new AndroidCommandCenter.Ci.LocalCiRunner(
            _runner, _git, _android, _testReports, sink);
        ci.LogMessage += L;
        void UpdateCiProgress(AndroidCommandCenter.Ci.CiRunProgress progress) =>
            Dispatcher.UIThread.Post(() =>
            {
                var stage = progress.Stage;
                bool completed = stage.Status is not AndroidCommandCenter.Ci.StageStatus.Running;
                string? detail = completed && !string.IsNullOrWhiteSpace(stage.Summary)
                    ? stage.Summary : null;
                ReportOperationProgress(progress.Current, progress.Total,
                    $"CI: {stage.Name}", completed, detail);
            });
        ci.ProgressChanged += UpdateCiProgress;

        string originalProject = _android.ProjectPath;
        string originalVariant = _android.SelectedVariant;
        string? originalTarget = _android.TargetDevice;
        EnterBusy(dryRun ? "Validating promotion" : "Running promotion CI",
            AndroidCommandCenter.Ci.LocalCiRunner.DefaultStages.Count + 1);
        Log($"\n===== Promotion CI{(dryRun ? " [DRY RUN]" : "")} =====");
        try
        {
            var manifest = await ci.RunAsync(cfg, _activeProfile,
                _profiles.GitHubToken(_activeProfile),
                triggeredBySchedule ? "schedule" : "gui", dryRun, _cts!.Token);
            Log($"Manifest: {manifest.ManifestPath}");
            Log($"===== {manifest.Result} =====");

            if (manifest.Success)
                _toasts.Success(dryRun ? "Promotion validation passed" : "Promotion CI completed",
                    manifest.Result, details: manifest.ManifestPath,
                    actionLabel: "Open runs", action: () => OpenDir(Paths.CiRuns));
            else
            {
                string? details = manifest.Stages.LastOrDefault(s => s.Failure != null)?.Failure?.ToString();
                _toasts.Error("Promotion CI failed", manifest.Result, details,
                    actionLabel: "Open runs", action: () => OpenDir(Paths.CiRuns));
            }
        }
        catch (OperationCanceledException)
        {
            Log("===== Promotion CI cancelled =====");
            _toasts.Warning("Promotion CI cancelled", "The remote was left unchanged unless promotion had already completed.");
        }
        catch (Exception ex)
        {
            Log($"[ERROR] {ex}");
            _toasts.Error("Promotion CI failed", Toaster.Friendly(ex), ex.ToString());
        }
        finally
        {
            ci.LogMessage -= L;
            ci.ProgressChanged -= UpdateCiProgress;
            _android.ProjectPath = originalProject;
            _android.SelectedVariant = originalVariant;
            _android.TargetDevice = originalTarget;
            ExitBusy();
        }
    }

    private async void RollbackPromotion_Click(object? sender, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;
        var run = AndroidCommandCenter.Ci.LocalCiRunner.LoadManifests()
            .FirstOrDefault(m => m.ProjectPath == _android.ProjectPath && m.Success && m.MergeSha != null);
        if (run is null)
        {
            _toasts.Warning("Nothing to roll back", "No successful promoted CI run was found for this project.");
            return;
        }

        var rollback = new AndroidCommandCenter.Ci.RollbackService(_git);
        rollback.LogMessage += L;
        AndroidCommandCenter.Ci.RollbackPlan plan;
        try
        {
            plan = await rollback.PlanAsync(_settings.Pipeline, run, _android.ProjectPath,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            rollback.LogMessage -= L;
            _toasts.Error("Rollback cannot start", Toaster.Friendly(ex), ex.ToString());
            return;
        }

        if (!await Dialogs.ConfirmDestructiveAsync(this, "Roll back the last promotion?",
                plan.Description + "\n\nThis creates and pushes a new revert commit; it never rewrites history.",
                "I understand this changes the production branch", "Create and push rollback"))
        {
            rollback.LogMessage -= L;
            return;
        }

        EnterBusy("Rolling back promotion");
        try
        {
            var result = await rollback.ExecuteAsync(_settings.Pipeline, plan,
                _android.ProjectPath, _activeProfile, _profiles.GitHubToken(_activeProfile),
                push: true, reason: "Manual rollback from Android Command Center", _cts!.Token);
            if (result.Success) _toasts.Success("Rollback completed", result.Message);
            else _toasts.Error("Rollback failed", result.Message);
        }
        catch (OperationCanceledException) { _toasts.Warning("Rollback cancelled"); }
        catch (Exception ex) { _toasts.Error("Rollback failed", Toaster.Friendly(ex), ex.ToString()); }
        finally
        {
            rollback.LogMessage -= L;
            ExitBusy();
        }
    }

    private void OpenCiRuns_Click(object? sender, RoutedEventArgs e) => OpenDir(Paths.CiRuns);

    private async Task CheckScheduledPromotion()
    {
        if (_isRunning || _configGate.IsHeld || !_settings.Pipeline.ScheduleEnabled) return;
        string? dueKey = GetDueScheduledRunKey(_settings.Pipeline, DateTime.Now);
        if (dueKey is null) return;

        _settings.Pipeline.LastScheduledRunKey = dueKey;
        _settings.Save();
        Log($"\n[schedule] promotion CI due ({dueKey}).");
        _toasts.Info("Scheduled promotion starting", dueKey);
        await RunPromotionCi(dryRun: false, triggeredBySchedule: true);
    }

    private static string? GetDueScheduledRunKey(CiPipelineConfig cfg, DateTime now)
    {
        if (!TryParseRecurringTimes(cfg.ScheduleTimes, out var times)) return null;
        foreach (var time in times)
        {
            var at = now.Date.Add(time);
            var age = now - at;
            if (age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(5))
            {
                string key = $"{at:yyyy-MM-dd}|{time:hh\\:mm}";
                return cfg.LastScheduledRunKey == key ? null : key;
            }
        }
        return null;
    }

    #endregion

    #region Automation

    private async void AutomationConfig_Click(object? s, RoutedEventArgs e)
    {
        if (!RequireProject()) return;
        AutomationWindow.AutomationDialogResult? result;
        using (await _configGate.AcquireAsync("Automation dialog"))
        {
            var a = _settings.Automation;
            var draft = new AutomationDraft(
                a.RemoteName, a.SourceBranch,
                _activeProfile.GitHubOwner, _activeProfile.GitHubRepo,
                _profiles.GitHubToken(_activeProfile),
                a.RecurringRunsEnabled, a.RecurringRunTimes, a.FailureAction,
                new EmailDraft(a.Email.SendEmail, a.Email.ToAddress, a.Email.FromAddress,
                    a.Email.SmtpHost, a.Email.SmtpPort, a.Email.EnableSsl, a.Email.UserName, ""));

            bool hasPw = _secrets.Get(EmailNotificationService.PasswordKey).Length > 0;
            result = await new AutomationWindow(draft, hasPw)
                .ShowDialog<AutomationWindow.AutomationDialogResult?>(this);
            if (result is null) { _toasts.Info("Automation config unchanged"); return; }

            try
            {
                result.Draft.ApplyTo(a, _activeProfile, _secrets);
                _profiles.SaveProfile(_activeProfile);
                _settings.Save();
                UpdateContextBanner();
                _toasts.Success("Automation config saved",
                    a.RecurringRunsEnabled ? $"Scheduled at {a.RecurringRunTimes}" : "Schedule disabled");
            }
            catch (Exception ex)
            {
                _toasts.Error("Could not save automation config", Toaster.Friendly(ex), ex.ToString());
                return;
            }
        }

        if (result.RunNow) await RunAutomatedDeployment(false);
    }

    private async void RunAutomatedDeployment_Click(object? s, RoutedEventArgs e) =>
        await RunAutomatedDeployment(false);

    private async Task RunAutomatedDeployment(bool triggeredBySchedule)
    {
        if (!RequireProject() || !RequireIdle()) return;

        // Snapshot under the gate so a dialog can't mutate config mid-run.
        using var gate = triggeredBySchedule
            ? _configGate.TryAcquire("Scheduled automated run")
            : await _configGate.AcquireAsync("Automated run");

        if (gate is null)
        {
            Log($"[schedule] skipped — config is being edited ({_configGate.HeldBy}).");
            _toasts.Warning("Scheduled run skipped",
                "Configuration was being edited when the run was due. It will retry at the next slot.");
            return;
        }

        var auto = _settings.Automation;
        var cfg = _settings.CiCd;

        var variants = cfg.BuildVariants.Count > 0
            ? cfg.BuildVariants.ToImmutableArray()
            : string.IsNullOrWhiteSpace(_android.SelectedVariant)
                ? []
                : [_android.SelectedVariant];

        var req = new PipelineRequest(
            _android.ProjectPath, _android.SelectedVariant, variants,
            _profiles.SigningFor(_activeProfile),
            new FirebaseSpec(_activeProfile.FirebaseCliPath, _activeProfile.FirebaseAppId,
                _activeProfile.FirebaseCredentialsJson, "",
                cfg.TesterGroups, cfg.TesterEmails, cfg.ReleaseNotes),
            new GitSourceSpec(true, auto.RemoteName, auto.SourceBranch,
                _activeProfile.GitHubOwner, _activeProfile.GitHubRepo,
                _profiles.GitHubToken(_activeProfile)),
            cfg.RunUnitTests, cfg.RunInstrumentTests, auto.FailureAction);

        if (req.Validate() is { Count: > 0 } errors)
        {
            foreach (var err in errors) Log($"[!] {err}");
            if (triggeredBySchedule)
                _toasts.Error("Scheduled run could not start",
                    $"{errors.Count} configuration problem(s).", details: string.Join('\n', errors));
            else
                await Dialogs.ErrorAsync(this, "Cannot start automated run", errors);
            return;
        }

        var result = await RunFullPipeline(req, "Automated Pipeline");
        await SendAutomationNotification(auto, result, showLocal: !triggeredBySchedule);
    }

    private async Task SendAutomationNotification(
        AutomationConfig auto, PipelineRunResult result, bool showLocal)
    {
        if (!auto.Email.SendEmail)
        {
            if (showLocal)
            {
                if (result.Success)
                    await Dialogs.SuccessAsync(this, "Automated run finished", result.Status, result.LogPath);
                else
                    await Dialogs.WarningAsync(this, "Automated run finished with problems",
                        result.Status, result.LogPath);
            }
            return;
        }

        EnterBusy("Sending notification");
        try
        {
            await _emailNotifications.SendPipelineLog(
                auto, ProjectName(), result.Success, result.Status, result.LogPath, CancellationToken.None);
            _toasts.Success("Notification emailed", auto.Email.ToAddress);
        }
        catch (Exception ex)
        {
            Log($"[WARN] Email failed: {ex.Message}");
            _toasts.Error("Could not send notification email", Toaster.Friendly(ex), ex.ToString());
        }
        finally { ExitBusy(); }
    }

    private async Task CheckScheduledAutomation()
    {
        if (_isRunning || _configGate.IsHeld || !_settings.Automation.RecurringRunsEnabled) return;
        string? dueKey = GetDueScheduledRunKey(_settings.Automation, DateTime.Now);
        if (dueKey == null) return;

        _settings.Automation.LastScheduledRunKey = dueKey;
        _settings.Save();
        Log($"\n[schedule] automated run due ({dueKey}).");
        _toasts.Info("Scheduled run starting", dueKey);
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
            {
                string k = $"{at:yyyy-MM-dd}|{t:hh\\:mm}";
                return a.LastScheduledRunKey == k ? null : k;
            }
        }
        return null;
    }

    private static bool TryParseRecurringTimes(string v, out List<TimeSpan> times)
    {
        times = [];
        var seen = new HashSet<TimeSpan>();
        foreach (var raw in v.Split([',', ';'],
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

    #endregion
}
