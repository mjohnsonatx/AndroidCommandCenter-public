using System.Diagnostics;

namespace AndroidCommandCenter.Services;

public class DeviceInfo
{
    public string Serial { get; init; } = "";
    public string State { get; init; } = "";
    public bool IsEmulator => Serial.StartsWith("emulator");
    public string DisplayName => IsEmulator ? $"{Serial} (emulator)" : $"{Serial} (device)";
}

public class AvdManager
{
    private static readonly TimeSpan ExistingEmulatorSettleDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartedEmulatorSettleDelay = TimeSpan.FromSeconds(30);

    private readonly ProcessRunner _runner;
    private readonly string _adbPath;
    private readonly string _emulatorPath;

    public event Action<string>? LogMessage;

    public AvdManager(ProcessRunner runner, string adbPath, string emulatorPath)
    {
        _runner = runner;
        _adbPath = adbPath;
        _emulatorPath = emulatorPath;
    }

    // ── AVD names (from SDK) ────────────────────
    public async Task<string[]> GetAvailableAvds(CancellationToken ct)
    {
        var (_, output) = await _runner.RunAndCaptureAsync(
            _emulatorPath, "-list-avds", ".", ct);

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s) && !s.StartsWith("["))
            .ToArray();
    }

    // ── Silent device list (no UI log noise) ────
    public async Task<List<DeviceInfo>> GetDevicesSilent(CancellationToken ct)
    {
        try
        {
            string output = await RunAdbSilent("devices", ct);
            return ParseDeviceOutput(output);
        }
        catch { return []; }
    }

    // ── Start / find emulator → return its serial ─
    public async Task<string?> EnsureEmulatorRunning(string? preferredAvd, CancellationToken ct)
    {
        var result = await EnsureEmulatorRunningCore(preferredAvd, ct);
        return result?.Serial;
    }

    public async Task<string?> EnsureEmulatorReadyForTests(string? preferredAvd, CancellationToken ct)
    {
        var result = await EnsureEmulatorRunningCore(preferredAvd, ct);
        if (result == null) return null;

        if (!await PrepareEmulatorForTests(result.Serial, result.StartedOrWasBooting, ct))
            return null;

        return result.Serial;
    }

    private async Task<EmulatorRunResult?> EnsureEmulatorRunningCore(string? preferredAvd, CancellationToken ct)
    {
        LogMessage?.Invoke("Checking for running emulators...");

        var existing = await GetDevicesSilent(ct);
        var existingEmulators = existing
            .Where(d => d.IsEmulator)
            .ToList();

        var runningEmulator = existingEmulators.FirstOrDefault(d => d.State == "device");
        if (runningEmulator != null)
        {
            LogMessage?.Invoke($"Emulator already running: {runningEmulator.Serial}");
            return await WaitForBoot(runningEmulator.Serial, ct)
                ? new EmulatorRunResult(runningEmulator.Serial, StartedOrWasBooting: false)
                : null;
        }

        if (existingEmulators.Count > 0)
        {
            var starting = existingEmulators[0];
            LogMessage?.Invoke(
                $"Emulator already starting: {starting.Serial} ({starting.State}). Waiting for it...");

            if (await WaitForEmulatorDevice(starting.Serial, ct, timeoutSeconds: 90) &&
                await WaitForBoot(starting.Serial, ct))
            {
                return new EmulatorRunResult(starting.Serial, StartedOrWasBooting: true);
            }

            LogMessage?.Invoke("[WARN] Existing emulator did not become ready. Trying to start an AVD.");
        }

        // Need to start one
        LogMessage?.Invoke("No emulator detected. Listing AVDs...");
        var avds = await GetAvailableAvds(ct);
        if (avds.Length == 0)
        {
            LogMessage?.Invoke("[ERROR] No AVDs found. Create one in Android Studio > Device Manager.");
            return null;
        }

        string avdName = (preferredAvd != null && avds.Contains(preferredAvd))
            ? preferredAvd
            : avds[0];

        LogMessage?.Invoke($"Starting AVD: {avdName}");
        var psi = new ProcessStartInfo
        {
            FileName = _emulatorPath,
            Arguments = $"-avd \"{avdName}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(psi);

        // Phase 1: wait for the new emulator serial to appear in adb devices
        LogMessage?.Invoke("Waiting for emulator to connect to ADB...");
        var knownSerials = existingEmulators
            .Select(d => d.Serial)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? serial = await WaitForNewEmulator(knownSerials, ct);
        if (serial == null)
        {
            LogMessage?.Invoke("[ERROR] Emulator never became ready in adb devices (60 s timeout).");
            return null;
        }

        // Phase 2: wait for boot_completed on that specific serial
        LogMessage?.Invoke($"Emulator {serial} connected. Waiting for boot...");
        return await WaitForBoot(serial, ct)
            ? new EmulatorRunResult(serial, StartedOrWasBooting: true)
            : null;
    }

    // ── Phase 1: detect new emulator serial ─────
    private async Task<string?> WaitForNewEmulator(
        HashSet<string> knownSerials, CancellationToken ct, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(2000, ct);

            var devices = await GetDevicesSilent(ct);
            string? newSerial = devices
                .Where(d => d.IsEmulator &&
                            d.State == "device" &&
                            !knownSerials.Contains(d.Serial))
                .Select(d => d.Serial)
                .FirstOrDefault();

            if (newSerial != null) return newSerial;
        }
        return null;
    }

    private async Task<bool> WaitForEmulatorDevice(
        string serial, CancellationToken ct, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(2000, ct);

            var devices = await GetDevicesSilent(ct);
            if (devices.Any(d =>
                    d.IsEmulator &&
                    d.State == "device" &&
                    string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    // ── Phase 2: poll boot on a SPECIFIC serial ─
    private async Task<bool> WaitForBoot(string serial, CancellationToken ct, int timeoutSeconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(3000, ct);

            try
            {
                string output = await RunAdbSilent(
                    $"-s {serial} shell getprop sys.boot_completed", ct);

                if (output.Trim().Contains("1"))
                {
                    LogMessage?.Invoke("Emulator booted successfully.");
                    return true;
                }
            }
            catch { /* device may reject shell before boot */ }

            LogMessage?.Invoke("  Still booting...");
        }

        LogMessage?.Invoke("[ERROR] Emulator boot timed out.");
        return false;
    }

    private async Task<bool> PrepareEmulatorForTests(
        string serial, bool startedOrWasBooting, CancellationToken ct)
    {
        LogMessage?.Invoke("Preparing emulator for instrument tests...");

        if (!await WaitForPackageManager(serial, ct))
        {
            LogMessage?.Invoke("[ERROR] Emulator package manager was not ready in time.");
            return false;
        }

        await WakeAndUnlock(serial, ct);

        var settleDelay = startedOrWasBooting
            ? StartedEmulatorSettleDelay
            : ExistingEmulatorSettleDelay;

        LogMessage?.Invoke(
            $"Letting emulator settle for {settleDelay.TotalSeconds:F0} seconds before instrument tests...");
        await Task.Delay(settleDelay, ct);

        LogMessage?.Invoke("Emulator is ready for instrument tests.");
        return true;
    }

    private async Task<bool> WaitForPackageManager(
        string serial, CancellationToken ct, int timeoutSeconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                string output = await RunAdbSilent(
                    $"-s {serial} shell pm path android", ct);

                if (output.Trim().StartsWith("package:", StringComparison.OrdinalIgnoreCase))
                {
                    LogMessage?.Invoke("Android package manager is ready.");
                    return true;
                }
            }
            catch { /* package service can reject commands during late boot */ }

            LogMessage?.Invoke("  Waiting for Android services...");
            await Task.Delay(3000, ct);
        }

        return false;
    }

    private async Task WakeAndUnlock(string serial, CancellationToken ct)
    {
        LogMessage?.Invoke("Waking emulator and dismissing keyguard...");

        await RunAdbSilent($"-s {serial} shell input keyevent KEYCODE_WAKEUP", ct);
        await RunAdbSilent($"-s {serial} shell wm dismiss-keyguard", ct);
        await RunAdbSilent($"-s {serial} shell input keyevent 82", ct);
    }

    // ── Helpers ─────────────────────────────────
    private async Task<string> RunAdbSilent(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _adbPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        Task<string> outputTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = proc.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(10_000);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { }
        }

        var completed = await Task.WhenAny(outputTask, Task.Delay(1000, CancellationToken.None));
        _ = errorTask;
        return completed == outputTask ? await outputTask : "";
    }

    private static List<DeviceInfo> ParseDeviceOutput(string output)
    {
        var devices = new List<DeviceInfo>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) ||
                trimmed.StartsWith("List") ||
                trimmed.StartsWith("*")) continue;

            var parts = trimmed.Split('\t');
            if (parts.Length >= 2)
                devices.Add(new DeviceInfo
                {
                    Serial = parts[0].Trim(),
                    State = parts[1].Trim()
                });
        }
        return devices;
    }

    private sealed record EmulatorRunResult(string Serial, bool StartedOrWasBooting);
}
