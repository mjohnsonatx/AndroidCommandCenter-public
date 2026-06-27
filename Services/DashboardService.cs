using System.Diagnostics;
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

public class DashboardService : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private long _prevIdle, _prevKernel, _prevUser;

    public event Action<SystemStats>? StatsUpdated;

    public DashboardService(double intervalMs = 2000)
    {
        _timer = new System.Timers.Timer(intervalMs);
        _timer.Elapsed += (_, _) => Poll();
        _timer.AutoReset = true;

        // Prime the CPU counters (first diff is always zero)
        GetSystemTimes(out _prevIdle, out _prevKernel, out _prevUser);
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Poll()
    {
        try
        {
            var mem = GetMemoryInfo();
            StatsUpdated?.Invoke(new SystemStats
            {
                CpuPercent = GetCpuPercent(),
                MemoryPercent = mem.Percent,
                MemoryTotalMb = mem.TotalMb,
                MemoryUsedMb = mem.UsedMb,
                JavaProcessCount = GetJavaCount()
            });
        }
        catch { /* dashboard is best-effort */ }
    }

    // ── CPU via GetSystemTimes delta ─────────────
    private float GetCpuPercent()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user))
            return 0;

        long di = idle - _prevIdle;
        long dk = kernel - _prevKernel;
        long du = user - _prevUser;
        _prevIdle = idle; _prevKernel = kernel; _prevUser = user;

        long total = dk + du;          // kernel includes idle
        return total == 0 ? 0 : Math.Clamp((1f - (float)di / total) * 100f, 0, 100);
    }

    // ── Memory via GlobalMemoryStatusEx ──────────
    private static (long TotalMb, long UsedMb, float Percent) GetMemoryInfo()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref ms)) return (0, 0, 0);

        long total = (long)(ms.ullTotalPhys / 1024 / 1024);
        long used = total - (long)(ms.ullAvailPhys / 1024 / 1024);
        return (total, used, ms.dwMemoryLoad);
    }

    // ── Java / Gradle daemon count ───────────────
    private static int GetJavaCount()
    {
        try { return Process.GetProcessesByName("java").Length; }
        catch { return 0; }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }

    // ── P/Invoke ─────────────────────────────────
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out long idleTime, out long kernelTime, out long userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}