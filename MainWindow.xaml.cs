using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private readonly AppSettings _settings;

    // ── UI collections ───────────────────────────
    private readonly ObservableCollection<string> _projects = new();
    private readonly ObservableCollection<string> _variants = new();

    // ── State ────────────────────────────────────
    private CancellationTokenSource? _cts;
    private bool _isRunning;

    // ── Pre-built brushes for status label ───────
    private static readonly SolidColorBrush IdleBrush =
        new(Color.FromRgb(0xa6, 0xe3, 0xa1));          // green
    private static readonly SolidColorBrush BusyBrush =
        new(Color.FromRgb(0xf9, 0xe2, 0xaf));          // yellow

    // ═══════════════════════════════════════════════
    //  CONSTRUCTOR
    // ═══════════════════════════════════════════════
    public MainWindow()
    {
        InitializeComponent();

        _android = new AndroidService(_runner);
        _avd = new AvdManager(_runner, _android.AdbPath, _android.EmulatorPath);
        _cache = new CacheManager(_runner, _android);

        // Pipe every log source into the UI
        _runner.OutputReceived += line => Dispatcher.Invoke(() => Log(line));
        _avd.LogMessage += line => Dispatcher.Invoke(() => Log(line));
        _cache.LogMessage += line => Dispatcher.Invoke(() => Log(line));
        _workflow.LogMessage += line => Dispatcher.Invoke(() => Log(line));
        _flavors.LogMessage += line => Dispatcher.Invoke(() => Log(line));

        // Dashboard
        _dashboard.StatsUpdated += s => Dispatcher.Invoke(() => UpdateDashboard(s));
        _dashboard.Start();

        // Load settings & populate dropdowns
        _settings = AppSettings.Load();
        ProjectCombo.ItemsSource = _projects;
        VariantCombo.ItemsSource = _variants;

        foreach (var p in _settings.ProjectPaths) _projects.Add(p);
        if (_settings.LastSelectedProject != null)
            ProjectCombo.SelectedItem = _settings.LastSelectedProject;

        Log("Android Command Center ready.");
        Log($"SDK: {_android.SdkPath}");
    }

    protected override void OnClosed(EventArgs e)
    {
        _dashboard.Dispose();
        base.OnClosed(e);
    }

    // ═══════════════════════════════════════════════
    //  LOGGING & DASHBOARD
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
    //  PROJECT MANAGEMENT
    // ═══════════════════════════════════════════════
    private void AddProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Android project root (contains gradlew.bat)"
        };
        if (dlg.ShowDialog() == true && !_projects.Contains(dlg.FolderName))
        {
            _projects.Add(dlg.FolderName);
            _settings.ProjectPaths = new List<string>(_projects);
            _settings.Save();
            ProjectCombo.SelectedItem = dlg.FolderName;
        }
    }

    private void RemoveProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectCombo.SelectedItem is string sel)
        {
            _projects.Remove(sel);
            _settings.ProjectPaths = new List<string>(_projects);
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
    //  VARIANT MANAGEMENT
    // ═══════════════════════════════════════════════
    private void LoadVariants(string project)
    {
        _variants.Clear();
        foreach (var v in _settings.GetVariants(project))
            _variants.Add(v);

        string prev = _settings.GetSelectedVariant(project);
        VariantCombo.SelectedItem =
            _variants.Contains(prev) ? prev : _variants.FirstOrDefault();
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

    private void AddVariant_Click(object sender, RoutedEventArgs e) => AddCustomVariant();

    private void CustomVariantBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddCustomVariant();
    }

    private void AddCustomVariant()
    {
        string raw = CustomVariantBox.Text.Trim();
        if (string.IsNullOrEmpty(raw)) return;

        string variant = char.ToUpper(raw[0]) + raw[1..];
        if (!_variants.Contains(variant))
        {
            _variants.Add(variant);
            if (!string.IsNullOrEmpty(_android.ProjectPath))
                _settings.SetVariants(_android.ProjectPath, [.. _variants]);
        }
        VariantCombo.SelectedItem = variant;
        CustomVariantBox.Clear();
        Log($"Added variant: {variant}");
    }

    // ═══════════════════════════════════════════════
    //  COMMAND RUNNER (shared plumbing)
    // ═══════════════════════════════════════════════
    private bool RequireProject()
    {
        if (!string.IsNullOrEmpty(_android.ProjectPath)) return true;
        Log("[!] Select a project first.");
        return false;
    }

    private bool RequireIdle()
    {
        if (!_isRunning) return true;
        Log("[!] Already running — cancel first.");
        return false;
    }

    private async Task RunOp(Func<CancellationToken, Task<int>> op, string name)
    {
        if (!RequireProject() || !RequireIdle()) return;

        _isRunning = true;
        _cts = new CancellationTokenSource();
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
        await RunOp(_android.RunUnitTests, "Unit Tests");

    private async void InstrumentTests_Click(object s, RoutedEventArgs e) =>
        await RunOp(_android.RunInstrumentTests, "Instrument Tests");

    // ═══════════════════════════════════════════════
    //  ENVIRONMENT
    // ═══════════════════════════════════════════════
    private async void Devices_Click(object s, RoutedEventArgs e) =>
        await RunOp(async ct =>
        {
            var (code, _) = await _android.ListDevices(ct);
            return code;
        }, "List Devices");

    private async void StartAvd_Click(object s, RoutedEventArgs e)
    {
        if (!RequireIdle()) return;
        _isRunning = true; _cts = new CancellationTokenSource();
        SetStatus("Starting AVD");
        try { await _avd.EnsureEmulatorRunning(_settings.PreferredAvd, _cts.Token); }
        catch (OperationCanceledException) { Log("AVD start cancelled."); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally { _isRunning = false; SetStatus("Idle"); }
    }

    private async void StopDaemons_Click(object s, RoutedEventArgs e) =>
        await RunOp(_android.StopDaemons, "Stop Daemons");

    private async void RestartAdb_Click(object s, RoutedEventArgs e) =>
        await RunOp(async ct =>
        {
            await _android.KillAdbServer(ct);
            return await _android.StartAdbServer(ct);
        }, "Restart ADB");

    // ═══════════════════════════════════════════════
    //  CACHE
    // ═══════════════════════════════════════════════
    private async void CacheLight_Click(object s, RoutedEventArgs e) =>
        await RunCacheClean(CacheCleanLevel.Light);

    private async void CacheMedium_Click(object s, RoutedEventArgs e) =>
        await RunCacheClean(CacheCleanLevel.Medium);

    private async void CacheHeavy_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "This deletes your global Gradle cache.\n" +
                "All dependencies re-download.\n\nContinue?",
                "Heavy Clean",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
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
        _isRunning = true;
        _cts = new CancellationTokenSource();
        SetStatus($"Cache ({level})");
        Log($"\n===== Cache Clean ({level}) =====");
        try { await _cache.CleanCaches(level, _cts.Token); }
        catch (OperationCanceledException) { Log("Cancelled."); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally { _isRunning = false; SetStatus("Idle"); }
    }

    // ═══════════════════════════════════════════════
    //  FULL TEST RUN WORKFLOW
    // ═══════════════════════════════════════════════
    private async void FullTestRun_Click(object s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;
        _isRunning = true;
        _cts = new CancellationTokenSource();
        SetStatus("Full Test Run");

        string v = _android.SelectedVariant;
        var steps = new List<WorkflowStep>
        {
            new() { Name = "Stop Daemons",
                    Action = _android.StopDaemons,
                    StopOnFailure = false },
            new() { Name = "Clean",
                    Action = _android.CleanBuild },
            new() { Name = "Invalidate Caches",
                    Action = async ct =>
                    {
                        await _cache.CleanCaches(CacheCleanLevel.Medium, ct);
                        return 0;
                    }},
            new() { Name = $"Unit Tests ({v})",
                    Action = _android.RunUnitTests,
                    MaxRetries = 2 },
            new() { Name = "Ensure Emulator",
                    Action = async ct =>
                        (await _avd.EnsureEmulatorRunning(
                            _settings.PreferredAvd, ct)) ? 0 : 1 },
            new() { Name = $"Instrumented Tests ({v})",
                    Action = _android.RunInstrumentTests,
                    MaxRetries = 2 }
        };

        try
        {
            bool ok = await _workflow.ExecuteAsync("Full Test Run", steps, _cts.Token);
            Log(ok ? "\nAll tests passed!" : "\nSome steps failed.");
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