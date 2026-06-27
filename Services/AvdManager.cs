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
        LogMessage?.Invoke("Checking for running emulators...");

        var existing = await GetDevicesSilent(ct);
        var runningEmulators = existing
            .Where(d => d.IsEmulator && d.State == "device")
            .Select(d => d.Serial)
            .ToHashSet();

        if (runningEmulators.Count > 0)
        {
            string first = runningEmulators.First();
            LogMessage?.Invoke($"Emulator already running: {first}");
            return first;
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
            Arguments = $"-avd {avdName}",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(psi);

        // Phase 1: wait for the new emulator serial to appear in adb devices
        LogMessage?.Invoke("Waiting for emulator to connect to ADB...");
        string? serial = await WaitForNewEmulator(runningEmulators, ct);
        if (serial == null)
        {
            LogMessage?.Invoke("[ERROR] Emulator never appeared in adb devices (60 s timeout).");
            return null;
        }

        // Phase 2: wait for boot_completed on that specific serial
        LogMessage?.Invoke($"Emulator {serial} connected. Waiting for boot...");
        return await WaitForBoot(serial, ct) ? serial : null;
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
                .Where(d => d.IsEmulator && !knownSerials.Contains(d.Serial))
                .Select(d => d.Serial)
                .FirstOrDefault();

            if (newSerial != null) return newSerial;
        }
        return null;
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
                    await Task.Delay(5000, ct);   // let system services settle
                    return true;
                }
            }
            catch { /* device may reject shell before boot */ }

            LogMessage?.Invoke("  Still booting...");
        }

        LogMessage?.Invoke("[ERROR] Emulator boot timed out.");
        return false;
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
        string output = await proc.StandardOutput.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(10_000);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try { await proc.WaitForExitAsync(linked.Token); }
        catch { try { proc.Kill(); } catch { } }

        return output;
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
}