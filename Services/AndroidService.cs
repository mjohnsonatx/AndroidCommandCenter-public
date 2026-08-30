using System.IO;
using AndroidCommandCenter.Models;

namespace AndroidCommandCenter.Services;

public sealed class AndroidService
{
    private readonly ProcessRunner _runner;

    public string ProjectPath { get; set; } = "";
    public string SelectedVariant { get; set; } = "Debug";
    public string? TargetDevice { get; set; }

    public string SdkPath { get; }
    public string AdbPath { get; }
    public string EmulatorPath { get; }
    public string SdkManagerPath { get; }

    public event Action<string>? LogMessage;

    public AndroidService(ProcessRunner runner)
    {
        _runner = runner;
        SdkPath = Platform.ResolveAndroidSdk();
        AdbPath = Platform.ResolveSdkTool(SdkPath, "platform-tools", "adb");
        EmulatorPath = Platform.ResolveSdkTool(SdkPath, "emulator", "emulator");
        SdkManagerPath = Platform.ResolveSdkTool(SdkPath, Path.Combine("cmdline-tools", "latest", "bin"), "sdkmanager");
    }

    /// <summary>Human-readable environment report for startup diagnostics.</summary>
    public IReadOnlyList<string> Diagnose()
    {
        var problems = new List<string>();
        if (!Directory.Exists(SdkPath))
            problems.Add($"Android SDK not found at '{SdkPath}'. Set ANDROID_HOME.");
        if (!File.Exists(AdbPath))
            problems.Add($"adb not found at '{AdbPath}'. Install platform-tools (sudo dnf install android-tools).");
        if (!File.Exists(EmulatorPath))
            problems.Add($"emulator not found at '{EmulatorPath}'. Emulator features will be unavailable.");
        if (Platform.Which("git") is null)
            problems.Add("git not found on PATH (sudo dnf install git).");
        if (Platform.Which("java") is null)
            problems.Add("java not found on PATH (sudo dnf install java-17-openjdk-devel).");
        return problems;
    }

    /// <summary>
    /// The project's Gradle wrapper. Throws a clear error rather than letting
    /// the process layer report "file not found" on a path the user never typed.
    /// </summary>
    private string Gradlew()
    {
        if (string.IsNullOrWhiteSpace(ProjectPath))
            throw new InvalidOperationException("No project selected.");
        var gw = Platform.GradleWrapper(ProjectPath)
            ?? throw new InvalidOperationException(
                $"No Gradle wrapper found in '{ProjectPath}'. Expected a 'gradlew' file — " +
                "is this the project root?");
        Platform.EnsureExecutable(gw);
        return gw;
    }

    private string Adb() =>
        File.Exists(AdbPath) ? AdbPath
        : throw new InvalidOperationException(
            $"adb not found at '{AdbPath}'. Install Android platform-tools or set ANDROID_HOME.");

    /// <summary>
    /// Flags applied to every Gradle invocation. --console=plain keeps the log
    /// readable (the rich console redraws with control codes), and
    /// --no-daemon is deliberately NOT set: daemons make repeat builds far
    /// faster and we stop them explicitly after test runs.
    /// </summary>
    private static readonly string[] CommonGradleFlags =
        ["--console=plain", "--stacktrace"];

    private List<string> GradleArgs(params string[] tasks)
    {
        var list = new List<string>(tasks);
        list.AddRange(CommonGradleFlags);
        return list;
    }

    // ── Build ──────────────────────────────────────────────────────────
    public Task<int> CleanBuild(CancellationToken ct) =>
        _runner.RunAsync(Gradlew(), GradleArgs("clean"), ProjectPath, ct);

    public Task<int> AssembleVariant(CancellationToken ct) =>
        _runner.RunAsync(Gradlew(), GradleArgs($"assemble{SelectedVariant}"), ProjectPath, ct);

    public Task<int> AssembleAll(CancellationToken ct) =>
        _runner.RunAsync(Gradlew(), GradleArgs("assemble"), ProjectPath, ct);

    public Task<int> ListTasks(CancellationToken ct) =>
        _runner.RunAsync(Gradlew(), GradleArgs("tasks", "--all"), ProjectPath, ct);

    // ── Lint (Android standard lint) ───────────────────────────────────
    /// <summary>
    /// Runs Android Lint for a variant. Uses lintRelease/lintDebug style task
    /// when a variant is given. -Dlint.baselines.continue lets a configured
    /// baseline file be created rather than failing the first run.
    /// </summary>
    public Task<int> RunLint(string variant, CancellationToken ct)
    {
        string task = string.IsNullOrWhiteSpace(variant) ? "lint" : $"lint{variant}";
        return _runner.RunAsync(Gradlew(), GradleArgs(task), ProjectPath, ct);
    }

    // ── Signed APK build ───────────────────────────────────────────────
    // Firebase/Play deploy builds ONLY. The android.injected.signing.*
    // properties override the signing config of EVERY build type, so reusing
    // this for a test build would change the certificate on the attached device
    // and force Android to uninstall (wiping app data) on next install.
    public Task<int> BuildSignedApk(SigningSpec signing, IEnumerable<string> variants, CancellationToken ct)
    {
        var selected = variants
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selected.Count == 0)
            throw new InvalidOperationException("Select at least one variant to build.");
        if (!signing.IsComplete)
            throw new InvalidOperationException(
                "Signing is not configured (keystore path/alias/passwords). Refusing to build a " +
                "deploy APK with default keys: deploying it would change the app's signing " +
                "certificate and wipe all app data on testers' devices when they install it.");

        var args = new List<string>();
        args.AddRange(selected.Select(v => $"assemble{v}"));
        args.AddRange(CommonGradleFlags);
        // Passed via ArgumentList, so passwords are never shell-parsed. The
        // runner's echo is suppressed because these contain secrets.
        args.Add($"-Pandroid.injected.signing.store.file={signing.KeystorePath}");
        args.Add($"-Pandroid.injected.signing.store.password={signing.StorePassword}");
        args.Add($"-Pandroid.injected.signing.key.alias={signing.KeyAlias}");
        args.Add($"-Pandroid.injected.signing.key.password={signing.KeyPassword}");

        LogMessage?.Invoke($"$ gradlew {string.Join(' ', selected.Select(v => "assemble" + v))} " +
                           "-Pandroid.injected.signing.* (redacted)");
        return _runner.RunAsync(Gradlew(), args, ProjectPath, ct, echoCommand: false);
    }

    // ── Tests ──────────────────────────────────────────────────────────
    // No injected signing here, deliberately: tests must build with the
    // project's normal per-build-type keys so a test install never changes the
    // certificate on the attached device.
    public Task<int> RunUnitTests(CancellationToken ct) =>
        _runner.RunAsync(Gradlew(), GradleArgs($"test{SelectedVariant}UnitTest"), ProjectPath, ct);

    public Task<int> RunInstrumentTests(CancellationToken ct)
    {
        var args = GradleArgs($"connected{SelectedVariant}AndroidTest");
        Dictionary<string, string>? env = null;
        if (TargetDevice != null)
        {
            args.Add($"-Pandroid.injected.connected.device.serial={TargetDevice}");
            env = new() { ["ANDROID_SERIAL"] = TargetDevice };
        }
        return _runner.RunAsync(Gradlew(), args, ProjectPath, ct, env);
    }

    // ── Daemons / ADB ──────────────────────────────────────────────────
    public Task<int> StopDaemons(CancellationToken ct) =>
        _runner.RunAsync(Gradlew(), ["--stop"], ProjectPath, ct);

    public Task<(int ExitCode, string Output)> ListDevices(CancellationToken ct) =>
        _runner.RunAndCaptureAsync(Adb(), ["devices", "-l"], ProjectPath, ct, echoCommand: true);

    public Task<int> KillAdbServer(CancellationToken ct) =>
        _runner.RunAsync(Adb(), ["kill-server"], ProjectPath, ct);

    public Task<int> StartAdbServer(CancellationToken ct) =>
        _runner.RunAsync(Adb(), ["start-server"], ProjectPath, ct);
}