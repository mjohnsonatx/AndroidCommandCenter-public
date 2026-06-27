using System.Diagnostics;

namespace AndroidCommandCenter.Services;

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

    public async Task<bool> IsEmulatorRunning(CancellationToken ct)
    {
        var (_, output) = await _runner.RunAndCaptureAsync(_adbPath, "devices", ".", ct);
        // "emulator-5554   device" means a booted emulator
        return output.Split('\n')
            .Any(l => l.Contains("emulator") && l.Contains("device"));
    }

    public async Task<string[]> GetAvailableAvds(CancellationToken ct)
    {
        var (_, output) = await _runner.RunAndCaptureAsync(
            _emulatorPath, "-list-avds", ".", ct);

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s) && !s.StartsWith("["))
            .ToArray();
    }

    public async Task<bool> EnsureEmulatorRunning(string? preferredAvd, CancellationToken ct)
    {
        LogMessage?.Invoke("Checking for running emulators...");

        if (await IsEmulatorRunning(ct))
        {
            LogMessage?.Invoke("Emulator already running.");
            return true;
        }

        LogMessage?.Invoke("No emulator detected. Listing AVDs...");
        var avds = await GetAvailableAvds(ct);

        if (avds.Length == 0)
        {
            LogMessage?.Invoke("[ERROR] No AVDs found. Create one in Android Studio > Device Manager.");
            return false;
        }

        // Use preferred AVD if it exists, otherwise take the first one
        string avdName = (preferredAvd != null && avds.Contains(preferredAvd))
            ? preferredAvd
            : avds[0];

        LogMessage?.Invoke($"Starting AVD: {avdName}");

        // Launch emulator as a detached background process
        var psi = new ProcessStartInfo
        {
            FileName = _emulatorPath,
            Arguments = $"-avd {avdName}",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(psi);

        LogMessage?.Invoke("Waiting for emulator to boot (timeout 120s)...");
        return await WaitForBoot(ct);
    }

    private async Task<bool> WaitForBoot(CancellationToken ct, int timeoutSeconds = 120)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(3000, ct);

            try
            {
                var (_, output) = await _runner.RunAndCaptureAsync(
                    _adbPath, "shell getprop sys.boot_completed", ".", ct);

                if (output.Trim().Contains("1"))
                {
                    LogMessage?.Invoke("Emulator booted successfully.");
                    await Task.Delay(5000, ct);   // let system services settle
                    return true;
                }
            }
            catch { /* device not ready yet — keep polling */ }

            LogMessage?.Invoke("  Still waiting...");
        }

        LogMessage?.Invoke($"[ERROR] Emulator boot timed out after {timeoutSeconds}s.");
        return false;
    }
}