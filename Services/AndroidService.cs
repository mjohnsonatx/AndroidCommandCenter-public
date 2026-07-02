using AndroidCommandCenter.Models;
using System.IO;

namespace AndroidCommandCenter.Services;

public class AndroidService
{
    private readonly ProcessRunner _runner;

    public string ProjectPath { get; set; } = "";
    public string SelectedVariant { get; set; } = "Debug";
    public string? TargetDevice { get; set; }        // null = all connected
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

    
    // ── Signed APK build (one or more variants) ─
    // Firebase-deploy builds ONLY. The android.injected.signing.* properties
    // override the signing config of EVERY build type (including debug), so this
    // method must never be reused for test or dev builds: an APK whose certificate
    // differs from the one already installed forces Android to uninstall first,
    // wiping all app data on the device. For the same reason a missing/broken
    // signing config is a hard failure here - silently falling back to Gradle's
    // default keys would deploy a differently-signed APK to Firebase testers.
    public Task<int> BuildSignedApk(CiCdConfig config, IEnumerable<string> variants, CancellationToken ct)
    {
        var selectedVariants = variants
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selectedVariants.Count == 0)
            throw new InvalidOperationException("Select at least one variant to build.");
        if (!config.IsSigningConfigured)
            throw new InvalidOperationException(
                "Signing is not configured (keystore path/alias/passwords). Refusing to build " +
                "a deploy APK with default keys: deploying it would change the app's signing " +
                "certificate and wipe all app data on testers' devices when they install it.");

        string args = string.Join(" ", selectedVariants.Select(v => $"assemble{v}"));
        args += $" -Pandroid.injected.signing.store.file=\"{config.KeystorePath}\"" +
                $" -Pandroid.injected.signing.store.password={config.StorePassword}" +
                $" -Pandroid.injected.signing.key.alias={config.KeyAlias}" +
                $" -Pandroid.injected.signing.key.password={config.KeyPassword}";
        return _runner.RunAsync(Gradlew, args, ProjectPath, ct);
    }

    // ── Build ──────────────────────────────────
    public Task<int> CleanBuild(CancellationToken ct) =>
        _runner.RunAsync(Gradlew, "clean", ProjectPath, ct);

    public Task<int> AssembleVariant(CancellationToken ct) =>
        _runner.RunAsync(Gradlew, $"assemble{SelectedVariant}", ProjectPath, ct);

    public Task<int> AssembleAll(CancellationToken ct) =>
        _runner.RunAsync(Gradlew, "assemble", ProjectPath, ct);

    // ── Tests ──────────────────────────────────
    // Test (and plain assemble) tasks intentionally receive NO injected signing
    // properties: they must build with the project's normal per-build-type keys
    // so a test install never changes the certificate on the attached device.
    public Task<int> RunUnitTests(CancellationToken ct) =>
        _runner.RunAsync(
            Gradlew, $"test{SelectedVariant}UnitTest", ProjectPath, ct);

    public Task<int> RunInstrumentTests(CancellationToken ct) =>
        _runner.RunAsync(
            Gradlew,
            TargetDevice != null
                ? $"connected{SelectedVariant}AndroidTest -Pandroid.injected.connected.device.serial={TargetDevice}"
                : $"connected{SelectedVariant}AndroidTest",
            ProjectPath,
            ct,
            TargetDevice != null
                ? new Dictionary<string, string> { ["ANDROID_SERIAL"] = TargetDevice }
                : null);

    // ── Daemons / ADB ──────────────────────────
    public Task<int> StopDaemons(CancellationToken ct) =>
        _runner.RunAsync(Gradlew, "--stop", ProjectPath, ct);

    public Task<(int ExitCode, string Output)> ListDevices(CancellationToken ct) =>
        _runner.RunAndCaptureAsync(AdbPath, "devices", ProjectPath, ct);

    public Task<int> KillAdbServer(CancellationToken ct) =>
        _runner.RunAsync(AdbPath, "kill-server", ProjectPath, ct);

    public Task<int> StartAdbServer(CancellationToken ct) =>
        _runner.RunAsync(AdbPath, "start-server", ProjectPath, ct);

    // ── Info ────────────────────────────────────
    public Task<int> ListTasks(CancellationToken ct) =>
        _runner.RunAsync(Gradlew, "tasks --all", ProjectPath, ct);

    private static string ResolveSdkPath() =>
        Environment.GetEnvironmentVariable("ANDROID_HOME")
        ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT")
        ?? Path.Combine(
               Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
               "Android", "Sdk");
}
