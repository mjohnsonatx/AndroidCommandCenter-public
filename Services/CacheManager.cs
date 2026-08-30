using System.IO;

namespace AndroidCommandCenter.Services;

public enum CacheCleanLevel
{
    Light,    // daemons + build dirs
    Medium,   // + .gradle, .idea caches, lock files
    Heavy     // + global ~/.gradle/caches (re-downloads everything)
}

public class CacheManager
{
    private readonly ProcessRunner _runner;
    private readonly AndroidService _android;

    public event Action<string>? LogMessage;
    private static string GradleHome() => Paths.GradleHome;

    public CacheManager(ProcessRunner runner, AndroidService android)
    {
        _runner = runner;
        _android = android;
    }

    public async Task CleanCaches(CacheCleanLevel level, CancellationToken ct)
    {
        string project = _android.ProjectPath;
        string gradleHome = GradleHome();

        // ── LIGHT ──────────────────────────────────────
        LogMessage?.Invoke("--- Stopping Gradle daemons ---");
        try { await _android.StopDaemons(ct); }
        catch { LogMessage?.Invoke("  gradlew --stop failed (continuing)"); }

        LogMessage?.Invoke("--- Deleting build directories ---");
        await Task.Run(() => DeleteBuildDirs(project), ct);

        if (level == CacheCleanLevel.Light) { Done("Light"); return; }

        // ── MEDIUM ─────────────────────────────────────
        LogMessage?.Invoke("--- Deleting project .gradle ---");
        await Task.Run(() => SafeDeleteDir(Path.Combine(project, ".gradle")), ct);

        LogMessage?.Invoke("--- Cleaning .idea caches ---");
        await Task.Run(() =>
        {
            SafeDeleteDir(Path.Combine(project, ".idea", "caches"));
            SafeDeleteDir(Path.Combine(project, ".idea", "libraries"));
        }, ct);

        LogMessage?.Invoke("--- Cleaning lock files ---");
        await Task.Run(() =>
        {
            DeleteLockFiles(gradleHome);
            DeleteLockFiles(Path.Combine(project, ".gradle"));
        }, ct);

        if (level == CacheCleanLevel.Medium) { Done("Medium"); return; }

        // ── HEAVY ──────────────────────────────────────
        LogMessage?.Invoke("--- Deleting global Gradle caches (will re-download!) ---");
        await Task.Run(() =>
        {
            SafeDeleteDir(Path.Combine(gradleHome, "caches"));
            SafeDeleteDir(Path.Combine(gradleHome, "daemon"));
        }, ct);

        LogMessage?.Invoke("--- Cleaning Android Studio caches ---");
        await Task.Run(CleanAndroidStudioCaches, ct);

        Done("Heavy");
    }

    public void CleanLockFilesOnly()
    {
        LogMessage?.Invoke("--- Cleaning lock files ---");
        DeleteLockFiles(GradleHome());
        DeleteLockFiles(Path.Combine(_android.ProjectPath, ".gradle"));
        LogMessage?.Invoke("Lock file cleanup complete.");
    }

    // ── Private helpers ────────────────────────────────

    private void Done(string level) =>
        LogMessage?.Invoke($"{level} cache clean complete.");

    private void DeleteBuildDirs(string projectPath)
    {
        SafeDeleteDir(Path.Combine(projectPath, "build"));

        try
        {
            foreach (var dir in Directory.GetDirectories(projectPath))
            {
                string name = Path.GetFileName(dir);
                if (name.StartsWith(".") || name == "gradle") continue;

                var buildDir = Path.Combine(dir, "build");
                if (Directory.Exists(buildDir))
                    SafeDeleteDir(buildDir);
            }
        }
        catch (Exception ex) { LogMessage?.Invoke($"  [WARN] {ex.Message}"); }
    }

    private void DeleteLockFiles(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.lock", SearchOption.AllDirectories))
            {
                try { File.Delete(f); LogMessage?.Invoke($"  Deleted: {f}"); }
                catch { /* locked — skip */ }
            }
            foreach (var f in Directory.EnumerateFiles(dir, "file-access.bin", SearchOption.AllDirectories))
            {
                try { File.Delete(f); LogMessage?.Invoke($"  Deleted: {f}"); }
                catch { }
            }
        }
        catch (Exception ex) { LogMessage?.Invoke($"  [WARN] {ex.Message}"); }
    }

    private void CleanAndroidStudioCaches()
    {
        var roots = new List<string>();
        if (Platform.IsWindows)
        {
            roots.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google"));
        }
        else
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(
                Environment.GetEnvironmentVariable("XDG_CACHE_HOME") ?? Path.Combine(home, ".cache"),
                "Google"));
            roots.Add(Path.Combine(home, ".cache", "JetBrains"));
        }
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(root)
                             .Where(d => Path.GetFileName(d)
                                 .StartsWith("AndroidStudio", StringComparison.OrdinalIgnoreCase)))
                {
                    // The whole dir under ~/.cache IS the cache on Linux.
                    SafeDeleteDir(Platform.IsWindows ? Path.Combine(dir, "system", "caches") : dir);
                }
            }
            catch (Exception ex) { LogMessage?.Invoke($"  [WARN] {ex.Message}"); }
        }
    }


    private void SafeDeleteDir(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            if (!Platform.IsWindows)
            {
                foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                    TryChmod(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    TryChmod(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            else
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(path, recursive: true);
            LogMessage?.Invoke($"  Deleted: {path}");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"  [WARN] Could not delete {path}: {ex.Message}");
        }
        static void TryChmod(string p, UnixFileMode mode)
        {
            try { File.SetUnixFileMode(p, File.GetUnixFileMode(p) | mode); } catch { }
        }
    }
}