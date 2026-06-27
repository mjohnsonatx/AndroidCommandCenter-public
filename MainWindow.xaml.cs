using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using AndroidCommandCenter.Models;
using AndroidCommandCenter.Services;

namespace AndroidCommandCenter;

public partial class MainWindow : Window
{
    private readonly ProcessRunner _runner = new();
    private readonly AndroidService _android;
    private readonly AvdManager _avd;
    private readonly CacheManager _cache;
    private readonly WorkflowEngine _workflow = new();
    private readonly AppSettings _settings;
    private readonly ObservableCollection<string> _projects = new();

    private CancellationTokenSource? _cts;
    private bool _isRunning;

    public MainWindow()
    {
        InitializeComponent();

        _android = new AndroidService(_runner);
        _avd = new AvdManager(_runner, _android.AdbPath, _android.EmulatorPath);
        _cache = new CacheManager(_runner, _android);

        // Pipe every log source into the UI log box
        _runner.OutputReceived += line => Dispatcher.Invoke(() => Log(line));
        _avd.LogMessage += line => Dispatcher.Invoke(() => Log(line));
        _cache.LogMessage += line => Dispatcher.Invoke(() => Log(line));
        _workflow.LogMessage += line => Dispatcher.Invoke(() => Log(line));

        // Load saved project list
        _settings = AppSettings.Load();
        ProjectCombo.ItemsSource = _projects;
        foreach (var p in _settings.ProjectPaths) _projects.Add(p);

        if (_settings.LastSelectedProject != null)
            ProjectCombo.SelectedItem = _settings.LastSelectedProject;

        Log("Android Command Center ready.");
        Log($"Android SDK : {_android.SdkPath}");
        Log($"ADB         : {_android.AdbPath}");
        Log($"Emulator    : {_android.EmulatorPath}");
    }

    // ════════════════════════════════════════════════════
    //  LOGGING
    // ════════════════════════════════════════════════════

    private void Log(string msg)
    {
        LogBox.AppendText(msg + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    // ════════════════════════════════════════════════════
    //  PROJECT MANAGEMENT
    // ════════════════════════════════════════════════════

    private void AddProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Android Project Root (the folder with gradlew.bat)"
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
        if (ProjectCombo.SelectedItem is string sel)
        {
            _android.ProjectPath = sel;
            _settings.LastSelectedProject = sel;
            _settings.Save();
            Log($"\nActive project: {sel}");
        }
    }

    // ════════════════════════════════════════════════════
    //  COMMAND RUNNER (shared by all single-command buttons)
    // ════════════════════════════════════════════════════

    private bool RequireProject()
    {
        if (!string.IsNullOrEmpty(_android.ProjectPath)) return true;
        Log("[!] Select a project first."); return false;
    }

    private bool RequireIdle()
    {
        if (!_isRunning) return true;
        Log("[!] A command is already running. Cancel it first."); return false;
    }

    private async Task RunOp(Func<CancellationToken, Task<int>> op, string name)
    {
        if (!RequireProject() || !RequireIdle()) return;

        _isRunning = true;
        _cts = new CancellationTokenSource();
        Log($"\n===== {name} =====");

        try
        {
            int code = await op(_cts.Token);
            Log($"===== {name} finished (exit {code}) =====");
        }
        catch (OperationCanceledException) { Log($"===== {name} cancelled ====="); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally { _isRunning = false; }
    }

    // ════════════════════════════════════════════════════
    //  BUILD BUTTONS
    // ════════════════════════════════════════════════════

    private async void Clean_Click(object s, RoutedEventArgs e) =>
        await RunOp(_android.CleanBuild, "Clean");

    private async void BuildDebug_Click(object s, RoutedEventArgs e) =>
        await RunOp(_android.BuildDebug, "Build Debug");

    private async void BuildRelease_Click(object s, RoutedEventArgs e) =>
        await RunOp(_android.BuildRelease, "Build Release");

    private async void ListTasks_Click(object s, RoutedEventArgs e) =>
        await RunOp(_android.ListTasks, "List Tasks");

    // ════════════════════════════════════════════════════
    //  TEST BUTTONS
    // ════════════════════════════════════════════════════

    private async void UnitTests_Click(object s, RoutedEventArgs e) =>
        await RunOp(_android.RunUnitTests, "Unit Tests");

    private async void InstrumentTests_Click(object s, RoutedEventArgs e) =>
        await RunOp(_android.RunInstrumentTests, "Instrument Tests");

    // ════════════════════════════════════════════════════
    //  ENVIRONMENT BUTTONS
    // ════════════════════════════════════════════════════

    private async void Devices_Click(object s, RoutedEventArgs e) =>
        await RunOp(async ct =>
        {
            var (code, _) = await _android.ListDevices(ct);
            return code;
        }, "List Devices");

    private async void StartAvd_Click(object s, RoutedEventArgs e)
    {
        if (!RequireIdle()) return;
        _isRunning = true;
        _cts = new CancellationTokenSource();
        try { await _avd.EnsureEmulatorRunning(_settings.PreferredAvd, _cts.Token); }
        catch (OperationCanceledException) { Log("AVD start cancelled."); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally { _isRunning = false; }
    }

    private async void StopDaemons_Click(object s, RoutedEventArgs e) =>
        await RunOp(_android.StopDaemons, "Stop Daemons");

    private async void RestartAdb_Click(object s, RoutedEventArgs e) =>
        await RunOp(async ct =>
        {
            await _android.KillAdbServer(ct);
            return await _android.StartAdbServer(ct);
        }, "Restart ADB");

    // ════════════════════════════════════════════════════
    //  CACHE BUTTONS
    // ════════════════════════════════════════════════════

    private async void CacheLight_Click(object s, RoutedEventArgs e) =>
        await RunCacheClean(CacheCleanLevel.Light);

    private async void CacheMedium_Click(object s, RoutedEventArgs e) =>
        await RunCacheClean(CacheCleanLevel.Medium);

    private async void CacheHeavy_Click(object s, RoutedEventArgs e)
    {
        var r = MessageBox.Show(
            "This deletes your global Gradle cache.\n" +
            "All dependencies re-download on next build.\n\nContinue?",
            "Heavy Cache Clean", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (r == MessageBoxResult.Yes)
            await RunCacheClean(CacheCleanLevel.Heavy);
    }

    private async void CleanLocks_Click(object s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;
        _isRunning = true;
        try { await Task.Run(() => _cache.CleanLockFilesOnly()); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally { _isRunning = false; }
    }

    private async Task RunCacheClean(CacheCleanLevel level)
    {
        if (!RequireProject() || !RequireIdle()) return;
        _isRunning = true;
        _cts = new CancellationTokenSource();
        Log($"\n===== Cache Clean ({level}) =====");
        try { await _cache.CleanCaches(level, _cts.Token); }
        catch (OperationCanceledException) { Log("Cache clean cancelled."); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); }
        finally { _isRunning = false; }
    }

    // ════════════════════════════════════════════════════
    //  WORKFLOW — FULL TEST RUN
    // ════════════════════════════════════════════════════

    private async void FullTestRun_Click(object s, RoutedEventArgs e)
    {
        if (!RequireProject() || !RequireIdle()) return;

        _isRunning = true;
        _cts = new CancellationTokenSource();

        var steps = new List<WorkflowStep>
        {
            new()
            {
                Name = "Stop Gradle Daemons",
                Action = _android.StopDaemons,
                StopOnFailure = false          // best-effort
            },
            new()
            {
                Name = "Clean Project",
                Action = _android.CleanBuild
            },
            new()
            {
                Name = "Invalidate Caches (Medium)",
                Action = async ct =>
                {
                    await _cache.CleanCaches(CacheCleanLevel.Medium, ct);
                    return 0;
                }
            },
            new()
            {
                Name = "Unit Tests",
                Action = _android.RunUnitTests,
                MaxRetries = 2
            },
            new()
            {
                Name = "Ensure Emulator Running",
                Action = async ct =>
                {
                    bool ok = await _avd.EnsureEmulatorRunning(
                        _settings.PreferredAvd, ct);
                    return ok ? 0 : 1;
                }
            },
            new()
            {
                Name = "Instrumented Tests",
                Action = _android.RunInstrumentTests,
                MaxRetries = 2
            }
        };

        try
        {
            bool ok = await _workflow.ExecuteAsync("Full Test Run", steps, _cts.Token);
            Log(ok ? "\nAll tests passed!" : "\nWorkflow had failures.");
        }
        catch (OperationCanceledException) { Log("\nWorkflow cancelled."); }
        catch (Exception ex) { Log($"\n[ERROR] {ex.Message}"); }
        finally { _isRunning = false; }
    }

    // ════════════════════════════════════════════════════
    //  UTILITY
    // ════════════════════════════════════════════════════

    private void Cancel_Click(object s, RoutedEventArgs e)
    {
        if (_cts != null) { _cts.Cancel(); Log("[!] Cancel requested..."); }
    }

    private void ClearLog_Click(object s, RoutedEventArgs e) => LogBox.Clear();
}