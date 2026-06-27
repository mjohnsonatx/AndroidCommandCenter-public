using System.IO;

namespace AndroidCommandCenter.Services;

public class AndroidService
{
    private readonly ProcessRunner _runner;

    public string ProjectPath { get; set; } = "";
    public string SdkPath { get; }
    public string AdbPath { get; }
    public string EmulatorPath { get; }

    public AndroidService(ProcessRunner runner)
    {
        _runner = runner;
        SdkPath = ResolveSdkPath();
        AdbPath = Path.Combine(SdkPath, "platform-tools", "adb.exe");
        EmulatorPath = Path.Combine(SdkPath, "emulator", "emulator.exe");
    }

    private string Gradlew => Path.Combine(ProjectPath, "gradlew.bat");

    // ── Build ──────────────────────────────────────────
    public Task<int> CleanBuild(CancellationToken ct) =>
        _runner.RunAsync(Gradlew, "clean", ProjectPath, ct);

    public Task<int> BuildDebug(CancellationToken ct) =>
        _runner.RunAsync(Gradlew, "assembleDebug", ProjectPath, ct);

    public Task<int> BuildRelease(CancellationToken ct) =>
        _runner.RunAsync(Gradlew, "assembleRelease", ProjectPath, ct);

    // ── Tests ──────────────────────────────────────────
    public Task<int> RunUnitTests(CancellationToken ct) =>
        _runner.RunAsync(Gradlew, "testDebugUnitTest", ProjectPath, ct);

    public Task<int> RunInstrumentTests(CancellationToken ct) =>
        _runner.RunAsync(Gradlew, "connectedDebugAndroidTest", ProjectPath, ct);

    // ── Daemons / ADB ──────────────────────────────────
    public Task<int> StopDaemons(CancellationToken ct) =>
        _runner.RunAsync(Gradlew, "--stop", ProjectPath, ct);

    public Task<(int ExitCode, string Output)> ListDevices(CancellationToken ct) =>
        _runner.RunAndCaptureAsync(AdbPath, "devices", ProjectPath, ct);

    public Task<int> KillAdbServer(CancellationToken ct) =>
        _runner.RunAsync(AdbPath, "kill-server", ProjectPath, ct);

    public Task<int> StartAdbServer(CancellationToken ct) =>
        _runner.RunAsync(AdbPath, "start-server", ProjectPath, ct);

    // ── Info ───────────────────────────────────────────
    public Task<int> ListTasks(CancellationToken ct) =>
        _runner.RunAsync(Gradlew, "tasks --all", ProjectPath, ct);

    // ── SDK resolution ─────────────────────────────────
    private static string ResolveSdkPath()
    {
        return Environment.GetEnvironmentVariable("ANDROID_HOME")
            ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT")
            ?? Path.Combine(
                   Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                   "Android", "Sdk");
    }
}