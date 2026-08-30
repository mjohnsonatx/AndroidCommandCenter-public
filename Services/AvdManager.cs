using System.Diagnostics;

namespace AndroidCommandCenter.Services;

public sealed class DeviceInfo
{
    public string Serial { get; init; } = "";
    public string State { get; init; } = "";
    public string Model { get; init; } = "";
    public bool IsEmulator =>
        Serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase) ||
        Serial.StartsWith("emulator_", StringComparison.OrdinalIgnoreCase);
    public bool IsOnline => State == "device";
    public string DisplayName =>
        (Model.Length > 0 ? $"{Model} ({Serial})" : Serial) +
        (IsEmulator ? " [emu]" : "") +
        (IsOnline ? "" : $" — {State}");
    /// <summary>Actionable text for the non-online states users actually hit.</summary>
    public string? Problem => State switch
    {
        "device"       => null,
        "unauthorized" => "Accept the USB debugging prompt on the device.",
        "offline"      => "Device is offline — reconnect the cable or restart adb.",
        "no permissions" => "udev permissions missing. Install android-udev-rules " +
                            "and ensure your user is in the 'plugdev' group, then replug.",
        _              => $"Device state '{State}'."
    };
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
        if (!File.Exists(_emulatorPath)) return [];
        var (_, output) = await _runner.RunAndCaptureAsync(
            _emulatorPath, ["-list-avds"], Path.GetTempPath(), ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0
                        && !s.StartsWith('[')
                        && !s.StartsWith("INFO")
                        && !s.StartsWith("WARNING")
                        && !s.Contains(' '))          // real AVD names have no spaces
            .ToArray();
    }

    // ── Silent device list (no UI log noise) ────
    public async Task<List<DeviceInfo>> GetDevicesSilent(CancellationToken ct)
    {
        try
        {
            string output = await RunAdbSilent(["devices", "-l"], ct);
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
        

        string avdName = (preferredAvd != null && avds.Contains(preferredAvd))
            ? preferredAvd
            : avds[0];

        
        if (!File.Exists(_emulatorPath))
        {
            LogMessage?.Invoke($"[ERROR] emulator binary not found at '{_emulatorPath}'.");
            return null;
        }
        
        if (!Platform.IsWindows && !File.Exists("/dev/kvm"))
            LogMessage?.Invoke("[WARN] /dev/kvm is missing — the emulator will be unusably slow or " +
                               "fail to start. Enable virtualisation in BIOS, or check " +
                               "`ls -l /dev/kvm` and that you are in the 'kvm' group.");
        else if (!Platform.IsWindows)
        {
            try
            {
                using var probe = File.Open("/dev/kvm", FileMode.Open, FileAccess.ReadWrite);
            }
            catch
            {
                LogMessage?.Invoke("[WARN] /dev/kvm exists but is not writable by your user. Run: " +
                                   "sudo usermod -aG kvm $USER  (then log out and back in).");
            }
        }

        LogMessage?.Invoke($"Starting AVD: {avdName}");
        var psi = new ProcessStartInfo(_emulatorPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-avd");
        psi.ArgumentList.Add(avdName);

        var emuProc = Process.Start(psi);
        if (emuProc != null)
        {
            // Surface emulator diagnostics instead of discarding them.
            emuProc.OutputDataReceived += (_, e) => { if (e.Data != null) LogMessage?.Invoke($"[emu] {e.Data}"); };
            emuProc.ErrorDataReceived  += (_, e) => { if (e.Data != null) LogMessage?.Invoke($"[emu] {e.Data}"); };
            emuProc.BeginOutputReadLine();
            emuProc.BeginErrorReadLine();
        }
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
                    ["-s", serial, "shell", "getprop", "sys.boot_completed"], ct);

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
                    ["-s", serial, "shell", "pm", "path", "android"], ct);

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

        await RunAdbSilent(["devices", "-l"], ct);
        await RunAdbSilent(["-s", serial, "shell", "getprop", "sys.boot_completed"], ct);
        await RunAdbSilent(["-s", serial, "shell", "input", "keyevent", "KEYCODE_WAKEUP"], ct);
    }

    // ── Helpers ─────────────────────────────────
    // Replace the whole RunAdbSilent method. The old one leaked processes on
// timeout, discarded stderr, and could return "" for a successful command
// whose output arrived after the 1 s race.
    private async Task<string> RunAdbSilent(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!File.Exists(_adbPath))
            throw new InvalidOperationException($"adb not found at '{_adbPath}'.");

        var psi = new ProcessStartInfo(_adbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException("Failed to start adb.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        var stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            ct.ThrowIfCancellationRequested();       // real cancel propagates
            return "";                                // our 15 s timeout: empty result
        }
        await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(2000, CancellationToken.None));
        return stdout.IsCompletedSuccessfully ? stdout.Result : "";
    }
    
    // Replace ParseDeviceOutput — `adb devices -l` is space-separated with key:value
    // extras, not tab-separated, so the old Split('\t') returned nothing for -l.
    private static List<DeviceInfo> ParseDeviceOutput(string output)
    {
        var devices = new List<DeviceInfo>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("List of devices")) continue;
            if (line.StartsWith('*')) continue;                  // daemon messages
            if (line.StartsWith("adb server")) continue;

            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            string model = "";
            foreach (var p in parts.Skip(2))
                if (p.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
                    model = p[6..].Replace('_', ' ');

            devices.Add(new DeviceInfo
            {
                Serial = parts[0],
                // "no permissions" arrives as two tokens; rejoin the tail.
                State = string.Join(' ', parts.Skip(1).TakeWhile(p => !p.Contains(':'))),
                Model = model
            });
        }
        return devices;
    }

    private sealed record EmulatorRunResult(string Serial, bool StartedOrWasBooting);
}
