using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AndroidCommandCenter.Models;
using AndroidCommandCenter.Services;

namespace AndroidCommandCenter;

public partial class MainWindow : Window
{
    // ── Services ─────────────────────────────────
    private readonly ProcessRunner _runner = new();
    private readonly AndroidService _android;
    private readonly AvdManager _avd;
    private readonly CacheManager _cache;
    private readonly WorkflowEngine _workflow = new();
    private readonly FlavorDetector _flavors = new();
    private readonly DashboardService _dashboard = new();
    private readonly TestReportService _testReports = new();
    private readonly AppSettings _settings;

    // ── UI collections ───────────────────────────
    private readonly ObservableCollection<string> _projects = new();
    private readonly ObservableCollection<string> _variants = new();
    private readonly ObservableCollection<string> _avds = new();
    private readonly ObservableCollection<string> _deviceSerials = new();

    // ── Timers ───────────────────────────────────
    private readonly DispatcherTimer _deviceTimer;

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
        _cache = new CacheManager(_runner, _android);

        _runner.OutputReceived += line => Dispatcher.Invoke(() => Log(line));
        _avd.LogMessage += line => Dispatcher.Invoke(() => Log(line));
        _cache.LogMessage += line => Dispatcher.Invoke(() => Log(line));
        _workflow.LogMessage += line => Dispatcher.Invoke(() => Log(line));
        _flavors.LogMessage += line => Dispatcher.Invoke(() => Log(line));
        _testReports.LogMessage += line => Dispatcher.Invoke(() => Log(line));

        _dashboard.StatsUpdated += s => Dispatcher.Invoke(() => UpdateDashboard(s));
        _dashboard.Start();

        _settings = AppSettings.Load();
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

        Log("Android Command Center ready.");
        Log($"SDK: {_android.SdkPath}");

        _ = LoadAvds();
        _ = RefreshDeviceList();
    }

    protected override void OnClosed(EventArgs e)
    {
        _deviceTimer.Stop();
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

        try
        {
            if (ensureEmulator)
            {
                Log("Ensuring emulator is running...");
                string? serial = await _avd.EnsureEmulatorRunning(
                    _settings.PreferredAvd, _cts.Token);

                if (serial == null)
                {
                    Log("[ERROR] No emulator available. Aborting.");
                    return;
                }

                // Auto-target the emulator so tests don't also run on physical devices
                _android.TargetDevice = serial;
                await RefreshDeviceList();
                if (_deviceSerials.Contains(serial))
                    DeviceCombo.SelectedItem = serial;

                Log($"Targeting device: {serial}");
            }

            Log($"\n===== {testType} ({_android.SelectedVariant}) =====");
            if (_android.TargetDevice != null)
                Log($"Target device: {_android.TargetDevice}");

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
            _isRunning = false;
            SetStatus("Idle");
        }
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
                        string? serial = await _avd.EnsureEmulatorRunning(_settings.PreferredAvd, ct);
                        if (serial == null) return 1;
                        _android.TargetDevice = serial;
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
        finally { _isRunning = false; SetStatus("Idle"); }
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