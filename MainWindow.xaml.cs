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
    private readonly FirebaseService _firebase;

    // ── Services ─────────────────────────────────
    private readonly ProcessRunner _runner = new();
    private readonly AndroidService _android;
    private readonly AvdManager _avd;
    private readonly GitService _git;
    private readonly CacheManager _cache;
    private readonly WorkflowEngine _workflow = new();
    private readonly FlavorDetector _flavors = new();
    private readonly DashboardService _dashboard = new();
    private readonly TestReportService _testReports = new();
    private readonly EmailNotificationService _emailNotifications = new();
    private readonly AppSettings _settings;
    private PipelineLogger? _pipelineLogger;

    // ── UI collections ───────────────────────────
    private readonly ObservableCollection<string> _projects = new();
    private readonly ObservableCollection<string> _variants = new();
    private readonly ObservableCollection<string> _avds = new();
    private readonly ObservableCollection<string> _deviceSerials = new();

    // ── Timers ───────────────────────────────────
    private readonly DispatcherTimer _deviceTimer;
    private readonly DispatcherTimer _automationTimer;

    // ── State ────────────────────────────────────
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _deviceRefreshing;

    private static readonly SolidColorBrush IdleBrush = new(Color.FromRgb(0xa6, 0xe3, 0xa1));
    private static readonly SolidColorBrush BusyBrush = new(Color.FromRgb(0xf9, 0xe2, 0xaf));

    // ═══════════════════════════════════════════════
    public MainWindow()
    {
        InitializeComponent();

        _android = new AndroidService(_runner);
        _avd = new AvdManager(_runner, _android.AdbPath, _android.EmulatorPath);
        _git = new GitService(_runner);
        _cache = new CacheManager(_runner, _android);

        _firebase = new FirebaseService(_runner);
        _firebase.LogMessage += line => Dispatcher.Invoke(() => Log(line));

        _runner.OutputReceived += line => Dispatcher.Invoke(() => Log(line));
        _avd.LogMessage += line => Dispatcher.Invoke(() => Log(line));
        _git.LogMessage += line => Dispatcher.Invoke(() => Log(line));
        _cache.LogMessage += line => Dispatcher.Invoke(() => Log(line));
        _workflow.LogMessage += line => Dispatcher.Invoke(() => Log(line));
        _flavors.LogMessage += line => Dispatcher.Invoke(() => Log(line));
        _testReports.LogMessage += line => Dispatcher.Invoke(() => Log(line));
        _emailNotifications.LogMessage += line => Dispatcher.Invoke(() => Log(line));

        _dashboard.StatsUpdated += s => Dispatcher.Invoke(() => UpdateDashboard(s));
        _dashboard.Start();

        _settings = AppSettings.Load();
        UpdateCiCdButtonStates();
        ProjectCombo.ItemsSource = _projects;
        VariantCombo.ItemsSource = _variants;
        AvdCombo.ItemsSource = _avds;
        DeviceCombo.ItemsSource = _deviceSerials;

        foreach (var p in _settings.ProjectPaths) _projects.Add(p);
        if (_settings.LastSelectedProject != null)
            ProjectCombo.SelectedItem = _settings.LastSelectedProject;

        // Device polling every 5 seconds
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

    protected override void OnClosed(EventArgs e)
    {
        _deviceTimer.Stop();
        _automationTimer.Stop();
        _dashboard.Dispose();
        base.OnClosed(e);
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
        CpuBar.Value = s.CpuPercent;
        CpuLabel.Text = $"{s.CpuPercent:F0}%";
        MemBar.Value = s.MemoryPercent;
        MemLabel.Text = $"{s.MemoryUsedMb:N0}/{s.MemoryTotalMb:N0} MB";
        DaemonLabel.Text = s.JavaProcessCount.ToString();
    }

    private async Task CheckScheduledAutomation()
    {
        if (_isRunning || !_settings.Automation.RecurringRunsEnabled) return;

        string? dueKey = GetDueScheduledRunKey(_settings.Automation, DateTime.Now);
        if (dueKey == null) return;

        _settings.Automation.LastScheduledRunKey = dueKey;
        _settings.Save();

        Log($"\nScheduled automated deployment triggered: {dueKey}");
        await RunAutomatedDeployment(triggeredBySchedule: true);
    }

    private static string? GetDueScheduledRunKey(AutomationConfig automation, DateTime now)
    {
        if (!TryParseRecurringTimes(automation.RecurringRunTimes, out var runTimes))
            return null;

        foreach (var time in runTimes)
        {
            var scheduledAt = now.Date.Add(time);
            var age = now - scheduledAt;
            if (age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(5))
            {
                string key = $"{scheduledAt:yyyy-MM-dd}|{time:hh\\:mm}";
                return automation.LastScheduledRunKey == key ? null : key;
            }
        }

        return null;
    }

    private static bool TryParseRecurringTimes(string value, out List<TimeSpan> runTimes)
    {
        runTimes = [];
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
                runTimes.Add(time);
        }

        runTimes.Sort();
        return runTimes.Count > 0;
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
                ? "(none)"
                : string.Join("   ", online.Select(d => d.DisplayName));

            string? prev = DeviceCombo.SelectedItem as string;
            _deviceSerials.Clear();
            _deviceSerials.Add("(All connected)");
            foreach (var d in online) _deviceSerials.Add(d.Serial);

            DeviceCombo.SelectedItem = _deviceSerials.Contains(prev ?? "")
                ? prev
                : "(All connected)";
        }
        catch { }
        finally { _deviceRefreshing = false; }
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceCombo.SelectedItem is string sel)
            _android.TargetDevice = sel == "(All connected)" ? null : sel;
    }

    // ═══════════════════════════════════════════════
    //  PROJECT MANAGEMENT
    // ═══════════════════════════════════════════════
    private void AddProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        { Title = "Select Android project root (contains gradlew.bat)" };
        if (dlg.ShowDialog() == true && !_projects.Contains(dlg.FolderName))
        {
            _projects.Add(dlg.FolderName);
            _settings.ProjectPaths = [.. _projects];
            _settings.Save();
            ProjectCombo.SelectedItem = dlg.FolderName;
        }
    }

    private void RemoveProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectCombo.SelectedItem is string sel)
        {
            _projects.Remove(sel);
            _settings.ProjectPaths = [.. _projects];
            _settings.Save();
        }
    }

    private void ProjectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectCombo.SelectedItem is not string sel) return;
        _android.ProjectPath = sel;
        _settings.LastSelectedProject = sel;
        _settings.Save();
        Log($"\nActive project: {sel}");
        LoadVariants(sel);
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

    private void VariantCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VariantCombo.SelectedItem is not string sel) return;
        _android.SelectedVariant = sel;
        if (!string.IsNullOrEmpty(_android.ProjectPath))
            _settings.SetSelectedVariant(_android.ProjectPath, sel);
    }

    private void DetectVariants_Click(object sender, RoutedEventArgs e)
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

    private void AvdCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AvdCombo.SelectedItem is string sel)
        { _settings.PreferredAvd = sel; _settings.Save(); }
    }

    private async void RefreshAvds_Click(object s, RoutedEventArgs e)
    { Log("Refreshing AVDs..."); await LoadAvds(); }

    private void OpenReports_Click(object s, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start("explorer.exe", TestReportService.ReportsDirectory);

    // ═══════════════════════════════════════════════
    //  COMMAND RUNNER
    // ═══════════════════════════════════════════════
    private bool RequireProject()
    {
        if (!string.IsNullOrEmpty(_android.ProjectPath)) return true;
        Log("[!] Select a project first."); return false;
    }
    private bool RequireIdle()
    {
        if (!_isRunning) return true;
        Log("[!] Already running — cancel first."); return false;
    }

    private async Task RunOp(Func<CancellationToken, Task<int>> op, string name)
    {
        if (!RequireProject() || !RequireIdle()) return;
        _isRunning = true; _cts = new CancellationTokenSource();
        SetStatus(name);
        Log($"\n===== {name} ({_android.SelectedVariant}) =====");
        try
        {
            int code = await op(_cts.Token);
            Log($"===== {name} exit {code} =====");
        }
        catch (OperationCanceledException) { Log($"===== {name} cancelled ====="); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally { _isRunning = false; SetStatus("Idle"); }
    }

    // ═══════════════════════════════════════════════
    //  BUILD
    // ═══════════════════════════════════════════════
    private async void Clean_Click(object s, RoutedEventArgs e) => await RunOp(_android.CleanBuild, "Clean");
    private async void Build_Click(object s, RoutedEventArgs e) => await RunOp(_android.AssembleVariant, $"Build {_android.SelectedVariant}");
    private async void BuildAll_Click(object s, RoutedEventArgs e) => await RunOp(_android.AssembleAll, "Build All");
    private async void ListTasks_Click(object s, RoutedEventArgs e) => await RunOp(_android.ListTasks, "List Tasks");

    // ═══════════════════════════════════════════════
    //  TESTS (with report + device targeting)
    // ═══════════════════════════════════════════════
    private async void UnitTests_Click(object s, RoutedEventArgs e) =>
        await RunTestsWithReport(_android.RunUnitTests, "UnitTests", ensureEmulator: false);

    private async void InstrumentTests_Click(object s, RoutedEventArgs e) =>
        await RunTestsWithReport(_android.RunInstrumentTests, "InstrumentTests", ensureEmulator: true);

    private async Task RunTestsWithReport(
        Func<CancellationToken, Task<int>> testOp, string testType, bool ensureEmulator)
    {
        if (!RequireProject() || !RequireIdle()) return;
        _isRunning = true; _cts = new CancellationTokenSource();
        SetStatus(testType);

        var buffer = new StringBuilder();
        void capture(string line) => buffer.AppendLine(line);
        _runner.OutputReceived += capture;
        bool gradleTestStarted = false;

        try
        {
            if (ensureEmulator)
            {
                string? serial = await PrepareInstrumentTestTarget(_cts.Token);
                if (serial == null) return;
            }

            Log($"\n===== {testType} ({_android.SelectedVariant}) =====");
            if (_android.TargetDevice != null)
                Log($"Target device: {_android.TargetDevice}");

            gradleTestStarted = true;
            int code = await testOp(_cts.Token);
            Log($"===== {testType} exit {code} =====");

            var report = _testReports.ParseResults(_android.ProjectPath, testType);
            ShowSummary(report);
            string path = _testReports.SaveReport(
                report, _android.ProjectPath, _android.SelectedVariant, buffer.ToString());
            Log($"Report saved: {path}");
        }
        catch (OperationCanceledException) { Log($"===== {testType} cancelled ====="); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally
        {
            _runner.OutputReceived -= capture;
            if (gradleTestStarted)
                await StopGradleResourcesAfterTesting(testType);
            _isRunning = false;
            SetStatus("Idle");
        }
    }

    private async Task StopGradleResourcesAfterTesting(string context, PipelineLogger? logger = null)
    {
        Log($"Stopping Gradle daemons after {context}...");
        logger?.Line($"Stopping Gradle daemons after {context}.");
        SetStatus("Stopping Gradle");

        try
        {
            using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            int code = await _android.StopDaemons(cleanupCts.Token);
            if (code == 0)
            {
                Log("Gradle daemons stopped after testing.");
                logger?.Line("Gradle daemons stopped after testing.");
            }
            else
            {
                Log($"[WARN] Gradle daemon stop returned exit code {code}.");
                logger?.Line($"Gradle daemon stop returned exit code {code}.");
            }
        }
        catch (OperationCanceledException)
        {
            Log("[WARN] Timed out stopping Gradle daemons after testing.");
            logger?.Line("Timed out stopping Gradle daemons after testing.");
        }
        catch (Exception ex)
        {
            Log($"[WARN] Failed to stop Gradle daemons after testing: {ex.Message}");
            logger?.Line($"Failed to stop Gradle daemons after testing: {ex.Message}");
        }
    }

    private async Task<string?> PrepareInstrumentTestTarget(CancellationToken ct, bool preferConnectedDevice = false)
    {
        if (preferConnectedDevice)
        {
            string? deviceSerial = await SelectConnectedPhysicalDevice(ct);
            if (deviceSerial != null)
                return deviceSerial;

            Log("No connected physical device found; falling back to emulator.");
        }

        Log("Ensuring emulator is ready for instrument tests...");
        string? serial = await _avd.EnsureEmulatorReadyForTests(_settings.PreferredAvd, ct);

        if (serial == null)
        {
            Log("[ERROR] No emulator available for instrument tests. Aborting.");
            return null;
        }

        _android.TargetDevice = serial;
        await RefreshDeviceList();
        if (_deviceSerials.Contains(serial))
            DeviceCombo.SelectedItem = serial;

        Log($"Targeting instrument tests to emulator: {serial}");
        return serial;
    }

    private async Task<string?> SelectConnectedPhysicalDevice(CancellationToken ct)
    {
        Log("Checking for connected physical devices...");

        var devices = await _avd.GetDevicesSilent(ct);
        var physicalDevices = devices
            .Where(d => d.State == "device" && !d.IsEmulator)
            .ToList();

        if (physicalDevices.Count == 0)
            return null;

        var selected = physicalDevices.FirstOrDefault(d =>
            string.Equals(d.Serial, _android.TargetDevice, StringComparison.OrdinalIgnoreCase))
            ?? physicalDevices[0];

        _android.TargetDevice = selected.Serial;
        await RefreshDeviceList();
        if (_deviceSerials.Contains(selected.Serial))
            DeviceCombo.SelectedItem = selected.Serial;

        Log($"Targeting instrument tests to connected device: {selected.Serial}");
        return selected.Serial;
    }

    private void ShowSummary(TestReport report)
    {
        Log("");
        Log("---------- TEST SUMMARY ----------");
        Log($"  Total:   {report.Total}");
        Log($"  Passed:  {report.Passed}");
        Log($"  Failed:  {report.Failed}");
        Log($"  Skipped: {report.Skipped}");
        Log($"  Time:    {report.TotalTimeSeconds:F2}s");
        if (report.Failures.Count > 0)
        {
            Log("");
            Log("  FAILURES:");
            foreach (var f in report.Failures)
            {
                Log($"   [X] {f.ClassName}.{f.Name}");
                if (f.FailureMessage != null) Log($"       {f.FailureMessage}");
            }
        }
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
        _isRunning = true; _cts = new CancellationTokenSource();
        SetStatus("Starting AVD");
        try
        {
            string? serial = await _avd.EnsureEmulatorRunning(_settings.PreferredAvd, _cts.Token);
            if (serial != null)
            {
                await RefreshDeviceList();
                if (_deviceSerials.Contains(serial))
                    DeviceCombo.SelectedItem = serial;
            }
        }
        catch (OperationCanceledException) { Log("AVD start cancelled."); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally { _isRunning = false; SetStatus("Idle"); }
    }

    private async void StopDaemons_Click(object s, RoutedEventArgs e) =>
        await RunOp(_android.StopDaemons, "Stop Daemons");

    private async void RestartAdb_Click(object s, RoutedEventArgs e) =>
        await RunOp(async ct =>
        { await _android.KillAdbServer(ct); return await _android.StartAdbServer(ct); },
        "Restart ADB");

    // ═══════════════════════════════════════════════
    //  CACHE
    // ═══════════════════════════════════════════════
    private async void CacheLight_Click(object s, RoutedEventArgs e) => await RunCacheClean(CacheCleanLevel.Light);
    private async void CacheMedium_Click(object s, RoutedEventArgs e) => await RunCacheClean(CacheCleanLevel.Medium);

    private async void CacheHeavy_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show("This deletes your global Gradle cache.\nAll deps re-download.\n\nContinue?",
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
        _isRunning = true; _cts = new CancellationTokenSource();
        SetStatus($"Cache ({level})");
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
        _isRunning = true; _cts = new CancellationTokenSource();
        SetStatus("Full Test Run");

        string v = _android.SelectedVariant;
        var steps = new List<WorkflowStep>
        {
            new() { Name = "Stop Daemons", Action = _android.StopDaemons, StopOnFailure = false },
            new() { Name = "Clean",        Action = _android.CleanBuild },
            new() { Name = "Invalidate Caches",
                    Action = async ct => { await _cache.CleanCaches(CacheCleanLevel.Medium, ct); return 0; } },
            new() { Name = $"Unit Tests ({v})",
                    Action = _android.RunUnitTests, MaxRetries = 2 },
            new() { Name = "Ensure Emulator",
                    Action = async ct =>
                    {
                        string? serial = await PrepareInstrumentTestTarget(ct);
                        if (serial == null) return 1;
                        return 0;
                    }},
            new() { Name = $"Instrumented Tests ({v})",
                    Action = _android.RunInstrumentTests, MaxRetries = 2 }
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
            _isRunning = false;
            SetStatus("Idle");
        }
    }

    // ═══════════════════════════════════════════════
    //  CI/CD
    // ═══════════════════════════════════════════════

    private string ProjectName() =>
        Path.GetFileName(_android.ProjectPath.TrimEnd('\\', '/'));

    private void UpdateCiCdButtonStates()
    {
        bool fb = _settings.CiCd.IsFirebaseConfigured;
        DeployFirebaseButton.IsEnabled = fb;
        DeployFirebaseButton.ToolTip = fb
            ? "Upload the built APK to Firebase App Distribution"
            : "Complete Firebase Config first (App ID + credentials JSON).";
    }

    private void FirebaseConfig_Click(object s, RoutedEventArgs e)
    {
        var dlg = new FirebaseConfigWindow(_settings.CiCd) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _firebase.CliPath = _settings.CiCd.FirebaseCliPath;
            _settings.Save();
            UpdateCiCdButtonStates();
            Log("Firebase config saved.");
        }
    }

    private async void BuildSigned_Click(object s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;

        var cfg = _settings.CiCd;
        var dlg = new BuildSignedApkWindow(cfg, _variants, _android.SelectedVariant) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _settings.Save();

        var logger = new PipelineLogger("BSA", ProjectName());
        _pipelineLogger = logger;
        _isRunning = true; _cts = new CancellationTokenSource();
        SetStatus("Build Signed APK");

        try
        {
            logger.Section("BUILD SIGNED APK");
            Log($"Variants: {string.Join(", ", cfg.BuildVariants)}");
            int code = await _android.BuildSignedApk(cfg, cfg.BuildVariants, _cts.Token);
            Log($"Build exit {code}");
            if (code == 0)
            {
                var apk = FindApk();
                Log(apk != null ? $"APK ready: {apk}" : "[WARN] APK not found.");
            }
        }
        catch (OperationCanceledException) { Log("Cancelled."); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally
        {
            logger.Save(); Log($"Log saved: {logger.FilePath}");
            _pipelineLogger = null; _isRunning = false; SetStatus("Idle");
        }
    }

    private async void DeployFirebase_Click(object s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;

        var cfg = _settings.CiCd;
        var dlg = new DeployFirebaseWindow(cfg, FindApk()) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _settings.Save();

        var apk = dlg.ApkPath;
        if (!File.Exists(apk)) { Log("[!] Select a valid APK file to deploy."); return; }

        var logger = new PipelineLogger("DEPLOY", ProjectName());
        _pipelineLogger = logger;
        _isRunning = true; _cts = new CancellationTokenSource();
        SetStatus("Deploying");

        try
        {
            logger.Section("DEPLOY TO FIREBASE");
            _firebase.CliPath = cfg.FirebaseCliPath;
            Log($"APK: {apk}");
            int code = await _firebase.Distribute(
                apk, cfg.FirebaseAppId, cfg.TesterGroups, cfg.TesterEmails,
                cfg.ReleaseNotes, cfg.FirebaseToken, cfg.CredentialsJsonPath, _cts.Token);
            Log($"Deploy exit {code}");
            if (code == 0) Log("Upload successful!");
        }
        catch (OperationCanceledException) { Log("Cancelled."); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally
        {
            logger.Save(); Log($"Log saved: {logger.FilePath}");
            _pipelineLogger = null; _isRunning = false; SetStatus("Idle");
        }
    }

    private async void AutomationConfig_Click(object s, RoutedEventArgs e)
    {
        var dlg = new AutomationWindow(_settings.Automation) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        _settings.Save();
        Log("Automation config saved.");

        if (dlg.RunNow)
            await RunAutomatedDeployment(triggeredBySchedule: false);
    }

    private async void RunAutomatedDeployment_Click(object s, RoutedEventArgs e) =>
        await RunAutomatedDeployment(triggeredBySchedule: false);

    private async Task RunAutomatedDeployment(bool triggeredBySchedule)
    {
        if (!RequireProject() || !RequireIdle()) return;

        var automation = CloneAutomationConfig(_settings.Automation);
        var cfg = CloneCiCdConfig(_settings.CiCd);
        cfg.FailureAction = automation.FailureAction;

        if (cfg.BuildVariants.Count == 0 && !string.IsNullOrWhiteSpace(_android.SelectedVariant))
            cfg.BuildVariants = [_android.SelectedVariant];

        if (!ValidateAutomatedDeploymentConfig(cfg, automation))
            return;

        PipelineRunResult result = await RunFullPipeline(
            cfg,
            async (logger, ct) =>
            {
                logger.Line($"Remote: {automation.RemoteName}");
                logger.Line($"Branch: {automation.SourceBranch}");
                int code = await _git.UpdateToRemoteBranch(
                    _android.ProjectPath,
                    automation.RemoteName,
                    automation.SourceBranch,
                    ct);
                return code == 0;
            },
            "Automated Pipeline");

        await SendAutomationNotification(automation, result, showLocalCompletion: !triggeredBySchedule);
    }

    private async void FullPipeline_Click(object s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;

        var cfg = _settings.CiCd;
        var dlg = new FullPipelineWindow(cfg, _variants, _android.SelectedVariant) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _settings.Save();
        UpdateCiCdButtonStates();

        await RunFullPipeline(cfg);
    }

    private bool ValidateAutomatedDeploymentConfig(CiCdConfig cfg, AutomationConfig automation)
    {
        if (!cfg.IsSigningConfigured)
        {
            Log("[!] Automated deployment needs signing configured first.");
            return false;
        }

        if (!cfg.IsFirebaseConfigured)
        {
            Log("[!] Automated deployment needs Firebase configured first.");
            return false;
        }

        if (cfg.BuildVariants.Count == 0)
        {
            Log("[!] Automated deployment needs at least one build variant.");
            return false;
        }

        if (automation.Email.SendEmail && !automation.CanSendEmail)
        {
            Log("[!] Automated deployment email settings are incomplete.");
            return false;
        }

        return true;
    }

    private async Task SendAutomationNotification(
        AutomationConfig automation,
        PipelineRunResult result,
        bool showLocalCompletion)
    {
        if (!automation.Email.SendEmail)
        {
            if (!showLocalCompletion)
            {
                Log($"Automated deployment finished: {result.Status}");
                Log($"Pipeline log: {result.LogPath}");
                return;
            }

            MessageBox.Show(
                $"Automated deployment finished: {result.Status}\n\nLog:\n{result.LogPath}",
                "Automated Deployment",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            return;
        }

        _isRunning = true;
        SetStatus("Sending notification");
        try
        {
            await _emailNotifications.SendPipelineLog(
                automation,
                ProjectName(),
                result.Success,
                result.Status,
                result.LogPath,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log($"[WARN] Failed to send pipeline log email: {ex.Message}");
        }
        finally
        {
            _isRunning = false;
            SetStatus("Idle");
        }
    }

    private static CiCdConfig CloneCiCdConfig(CiCdConfig source) => new()
    {
        KeystorePath = source.KeystorePath,
        KeyAlias = source.KeyAlias,
        EncryptedStorePassword = source.EncryptedStorePassword,
        EncryptedKeyPassword = source.EncryptedKeyPassword,
        BuildVariants = source.BuildVariants.ToList(),
        FirebaseCliPath = source.FirebaseCliPath,
        FirebaseAppId = source.FirebaseAppId,
        CredentialsJsonPath = source.CredentialsJsonPath,
        EncryptedFirebaseToken = source.EncryptedFirebaseToken,
        TesterGroups = source.TesterGroups,
        TesterEmails = source.TesterEmails,
        ReleaseNotes = source.ReleaseNotes,
        RunUnitTests = source.RunUnitTests,
        RunInstrumentTests = source.RunInstrumentTests,
        FailureAction = source.FailureAction
    };

    private static AutomationConfig CloneAutomationConfig(AutomationConfig source) => new()
    {
        RemoteName = source.RemoteName,
        SourceBranch = source.SourceBranch,
        RecurringRunsEnabled = source.RecurringRunsEnabled,
        RecurringRunTimes = source.RecurringRunTimes,
        LastScheduledRunKey = source.LastScheduledRunKey,
        FailureAction = source.FailureAction,
        Email = new EmailNotificationConfig
        {
            SendEmail = source.Email.SendEmail,
            SmtpHost = source.Email.SmtpHost,
            SmtpPort = source.Email.SmtpPort,
            EnableSsl = source.Email.EnableSsl,
            UserName = source.Email.UserName,
            EncryptedPassword = source.Email.EncryptedPassword,
            FromAddress = source.Email.FromAddress,
            ToAddress = source.Email.ToAddress
        }
    };

    private async Task<PipelineRunResult> RunFullPipeline(
        CiCdConfig cfg,
        Func<PipelineLogger, CancellationToken, Task<bool>>? preflight = null,
        string statusText = "Full Pipeline")
    {
        var logger = new PipelineLogger("FP", ProjectName());
        _pipelineLogger = logger;
        _isRunning = true; _cts = new CancellationTokenSource();
        SetStatus(statusText);
        var ct = _cts.Token;
        bool pipelineTestsStarted = false;
        bool pipelineTestGradleCleanupDone = false;

        try
        {
            if (preflight != null)
            {
                logger.Section("SYNC SOURCE");
                if (!await preflight(logger, ct))
                {
                    Log("[ABORT] Source update failed.");
                    return new PipelineRunResult(false, "Source update failed", logger.FilePath);
                }
            }

            // ── Stop daemons ──
            logger.Section("STOP DAEMONS");
            await _android.StopDaemons(ct);

            // ── Clean ──
            logger.Section("CLEAN");
            if (await _android.CleanBuild(ct) != 0)
            {
                Log("[ABORT] Clean failed.");
                return new PipelineRunResult(false, "Clean failed", logger.FilePath);
            }

            // ── Unit tests ──
            if (cfg.RunUnitTests)
            {
                pipelineTestsStarted = true;
                logger.Section("UNIT TESTS");
                int code = await _android.RunUnitTests(ct);
                var report = _testReports.ParseResults(_android.ProjectPath, "UnitTests");
                ShowSummary(report);
                _testReports.SaveReport(report, _android.ProjectPath, _android.SelectedVariant, "(FP unit)");

                if (report.Failed > 0 || code != 0)
                    if (!await HandleTestFailure("Unit Tests", report, cfg.FailureAction, logger))
                    {
                        Log("[ABORT] Stopped by failure policy.");
                        return new PipelineRunResult(false, "Stopped after unit test failure", logger.FilePath);
                    }
            }

            // ── Instrument tests ──
            if (cfg.RunInstrumentTests)
            {
                pipelineTestsStarted = true;
                logger.Section("SELECT INSTRUMENT TEST TARGET");
                string? serial = await PrepareInstrumentTestTarget(ct, preferConnectedDevice: true);
                if (serial == null)
                {
                    Log("[ABORT] No instrument test target.");
                    return new PipelineRunResult(false, "No instrument test target available", logger.FilePath);
                }

                logger.Section("INSTRUMENT TESTS");
                int code = await _android.RunInstrumentTests(ct);
                var report = _testReports.ParseResults(_android.ProjectPath, "InstrumentTests");
                ShowSummary(report);
                _testReports.SaveReport(report, _android.ProjectPath, _android.SelectedVariant, "(FP instr)");

                if (report.Failed > 0 || code != 0)
                    if (!await HandleTestFailure("Instrument Tests", report, cfg.FailureAction, logger))
                    {
                        Log("[ABORT] Stopped by failure policy.");
                        return new PipelineRunResult(false, "Stopped after instrument test failure", logger.FilePath);
                    }
            }

            if (pipelineTestsStarted)
            {
                logger.Section("STOP TEST GRADLE DAEMONS");
                await StopGradleResourcesAfterTesting("pipeline tests", logger);
                pipelineTestGradleCleanupDone = true;
                SetStatus(statusText);
            }

            // ── Build signed APK ──
            logger.Section("BUILD SIGNED APK");
            Log($"Variants: {string.Join(", ", cfg.BuildVariants)}");
            if (await _android.BuildSignedApk(cfg, cfg.BuildVariants, ct) != 0)
            {
                Log("[ABORT] Build failed.");
                return new PipelineRunResult(false, "Build failed", logger.FilePath);
            }

            var apk = FindApk();
            if (apk == null)
            {
                Log("[ABORT] APK not found after build.");
                return new PipelineRunResult(false, "APK not found after build", logger.FilePath);
            }
            Log($"APK: {apk}");

            // ── Deploy ──
            logger.Section("DEPLOY TO FIREBASE");
            _firebase.CliPath = cfg.FirebaseCliPath;
            int deploy = await _firebase.Distribute(
                apk, cfg.FirebaseAppId, cfg.TesterGroups, cfg.TesterEmails,
                cfg.ReleaseNotes, cfg.FirebaseToken, cfg.CredentialsJsonPath, ct);

            Log(deploy == 0 ? "\nPIPELINE COMPLETE — deployed successfully!"
                            : "\nPIPELINE FINISHED — deploy returned non-zero.");

            return new PipelineRunResult(
                deploy == 0,
                deploy == 0 ? "Deployed successfully" : $"Deploy returned {deploy}",
                logger.FilePath);
        }
        catch (OperationCanceledException)
        {
            Log("\nPipeline cancelled.");
            return new PipelineRunResult(false, "Pipeline cancelled", logger.FilePath);
        }
        catch (Exception ex)
        {
            Log($"\n[ERROR] {ex.Message}");
            return new PipelineRunResult(false, ex.Message, logger.FilePath);
        }
        finally
        {
            if (pipelineTestsStarted && !pipelineTestGradleCleanupDone)
            {
                logger.Section("STOP TEST GRADLE DAEMONS");
                await StopGradleResourcesAfterTesting("pipeline tests", logger);
                pipelineTestGradleCleanupDone = true;
            }

            await RefreshDeviceList();
            logger.Save();
            Log($"Pipeline log saved: {logger.FilePath}");
            _pipelineLogger = null;
            _isRunning = false; SetStatus("Idle");
        }
    }

    private sealed record PipelineRunResult(bool Success, string Status, string LogPath);

    private async Task<bool> HandleTestFailure(
        string stage, TestReport report, TestFailureAction action, PipelineLogger logger)
    {
        string details = BuildFailureDetails(report);
        logger.Line($"[{stage}] FAILED — {report.Failed}/{report.Total} test(s) failed.");
        logger.Line(details);

        switch (action)
        {
            case TestFailureAction.SilentFailure:
                Log("[Policy] Silent failure — continuing.");
                logger.Line("DECISION: Silent policy — continued automatically.");
                return true;

            case TestFailureAction.AutomaticBounceBack:
                Log("[Policy] Automatic bounce back — stopping pipeline.");
                logger.Line("DECISION: Automatic bounce back — pipeline stopped.");
                return false;

            case TestFailureAction.Warning:
            case TestFailureAction.WarningWithConfirm:
                {
                    bool requireConfirm = action == TestFailureAction.WarningWithConfirm;
                    var dlg = new TestFailureDialog($"{stage} Failed", details, requireConfirm) { Owner = this };
                    bool proceed = dlg.ShowDialog() == true;
                    Log($"[Policy] {(requireConfirm ? "Warning+confirm" : "Warning")} — user chose to {(proceed ? "CONTINUE" : "STOP")}.");
                    logger.Line($"DECISION: User chose to {(proceed ? "CONTINUE" : "STOP")}.");
                    return proceed;
                }
        }
        return false;
    }

    private static string BuildFailureDetails(TestReport report)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Failed {report.Failed} of {report.Total} tests.");
        foreach (var f in report.Failures)
        {
            sb.AppendLine();
            sb.AppendLine($"[X] {f.ClassName}.{f.Name}");
            if (f.FailureType != null) sb.AppendLine($"    Type:    {f.FailureType}");
            if (f.FailureMessage != null) sb.AppendLine($"    Message: {f.FailureMessage}");
            if (!string.IsNullOrWhiteSpace(f.StackTrace))
            {
                sb.AppendLine("    Stack trace:");
                foreach (var line in f.StackTrace.Split('\n').Take(15))
                    if (!string.IsNullOrWhiteSpace(line))
                        sb.AppendLine($"      {line.TrimEnd()}");
            }
        }
        return sb.ToString();
    }

    private string? FindApk()
    {
        var root = Path.Combine(_android.ProjectPath, "app", "build", "outputs", "apk");
        if (!Directory.Exists(root)) return null;
        return Directory.GetFiles(root, "*.apk", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Contains("unsigned"))
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
    }



    // ═══════════════════════════════════════════════
    //  UTILITY
    // ═══════════════════════════════════════════════
    private void Cancel_Click(object s, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); Log("[!] Cancel requested..."); }
    }

    private void ClearLog_Click(object s, RoutedEventArgs e) => LogBox.Clear();
}
