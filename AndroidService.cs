// AndroidService.cs
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public class AndroidService
{
    private readonly ProcessRunner _runner;
    public string ProjectPath { get; set; } = "";

    // Configure these once (or auto-detect from ANDROID_HOME env var)
    public string AdbPath { get; set; } = ExpandSdk("platform-tools\\adb.exe");
    public string EmulatorPath { get; set; } = ExpandSdk("emulator\\emulator.exe");

    public AndroidService(ProcessRunner runner) => _runner = runner;

    private string Gradlew => Path.Combine(ProjectPath, "gradlew.bat");

    public Task<int> CleanBuild(CancellationToken ct) =>
        _runner.RunAsync(Gradlew, "clean", ProjectPath, ct);

    public Task<int> UnitTests(CancellationToken ct) =>
        _runner.RunAsync(Gradlew, "testDebugUnitTest", ProjectPath, ct);

    public Task<int> InstrumentTests(CancellationToken ct) =>
        _runner.RunAsync(Gradlew, "connectedDebugAndroidTest", ProjectPath, ct);

    public Task<int> StopDaemons(CancellationToken ct) =>
        _runner.RunAsync(Gradlew, "--stop", ProjectPath, ct);

    public Task<int> ListDevices(CancellationToken ct) =>
        _runner.RunAsync(AdbPath, "devices", ProjectPath, ct);

    public Task<int> BuildSignedApk(CancellationToken ct) =>
        _runner.RunAsync(Gradlew, "assembleRelease", ProjectPath, ct);

    private static string ExpandSdk(string rel)
    {
        var home = System.Environment.GetEnvironmentVariable("ANDROID_HOME")
                   ?? System.Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT")
                   ?? Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                        "Android", "Sdk");
        return Path.Combine(home, rel);
    }
}