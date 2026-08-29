using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace AndroidCommandCenter.Services;

public class SystemStats
{
    public float CpuPercent { get; init; }
    public float MemoryPercent { get; init; }
    public long MemoryUsedMb { get; init; }
    public long MemoryTotalMb { get; init; }
    public int JavaProcessCount { get; init; }
}

public sealed class DashboardService : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private ulong _prevIdle, _prevTotal;

    public event Action<SystemStats>? StatsUpdated;
    /// <summary>Raised once if sampling fails, so the UI can grey the gauges.</summary>
    public event Action<string>? Degraded;
    private bool _reportedDegraded;

    public DashboardService(double intervalMs = 2000)
    {
        _timer = new System.Timers.Timer(intervalMs) { AutoReset = true };
        _timer.Elapsed += (_, _) => Poll();
        ReadCpu(out _prevIdle, out _prevTotal);   // prime
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Poll()
    {
        try
        {
            var (total, used, pct) = ReadMemory();
            StatsUpdated?.Invoke(new SystemStats
            {
                CpuPercent = CpuPercent(),
                MemoryTotalMb = total,
                MemoryUsedMb = used,
                MemoryPercent = pct,
                JavaProcessCount = JavaCount()
            });
        }
        catch (Exception ex)
        {
            if (!_reportedDegraded)
            {
                _reportedDegraded = true;
                Degraded?.Invoke($"System monitor unavailable: {ex.Message}");
            }
        }
    }

    private float CpuPercent()
    {
        if (!ReadCpu(out ulong idle, out ulong total)) return 0;
        ulong dIdle = idle - _prevIdle, dTotal = total - _prevTotal;
        _prevIdle = idle; _prevTotal = total;
        if (dTotal == 0) return 0;
        return Math.Clamp((1f - (float)dIdle / dTotal) * 100f, 0, 100);
    }

    /// <summary>First line of /proc/stat: cpu user nice system idle iowait ...</summary>
    private static bool ReadCpu(out ulong idle, out ulong total)
    {
        idle = total = 0;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
        try
        {
            var line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu "));
            if (line is null) return false;
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
                        .Select(s => ulong.TryParse(s, out var v) ? v : 0UL).ToArray();
            if (f.Length < 5) return false;
            idle = f[3] + f[4];                       // idle + iowait
            total = f.Aggregate(0UL, (a, b) => a + b);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// MemAvailable (not MemFree) is the kernel's own estimate of what a new
    /// workload can claim; MemFree excludes reclaimable page cache and would
    /// report ~95% used on any warm machine.
    /// </summary>
    private static (long TotalMb, long UsedMb, float Percent) ReadMemory()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return (0, 0, 0);
        long total = 0, avail = 0;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:")) total = ParseKb(line);
            else if (line.StartsWith("MemAvailable:")) avail = ParseKb(line);
            if (total > 0 && avail > 0) break;
        }
        if (total == 0) return (0, 0, 0);
        long totalMb = total / 1024, usedMb = (total - avail) / 1024;
        return (totalMb, usedMb, totalMb == 0 ? 0 : usedMb * 100f / totalMb);

        static long ParseKb(string l)
        {
            var parts = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], out var v) ? v : 0;
        }
    }

    /// <summary>
    /// Gradle daemons. Process name on Linux is "java"; also catch the
    /// GradleDaemon process some JDKs name differently.
    /// </summary>
    private static int JavaCount()
    {
        try
        {
            return Process.GetProcesses().Count(p =>
            {
                try
                {
                    return p.ProcessName.Equals("java", StringComparison.OrdinalIgnoreCase)
                        || p.ProcessName.Contains("Gradle", StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
                finally { p.Dispose(); }
            });
        }
        catch { return 0; }
    }

    public void Dispose() { _timer.Stop(); _timer.Dispose(); }
}