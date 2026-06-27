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
        string google = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google");
        if (!Directory.Exists(google)) return;

        try
        {
            foreach (var dir in Directory.GetDirectories(google, "AndroidStudio*"))
                SafeDeleteDir(Path.Combine(dir, "system", "caches"));
        }
        catch (Exception ex) { LogMessage?.Invoke($"  [WARN] {ex.Message}"); }
    }

    private void SafeDeleteDir(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            // Clear read-only flags first (common with Gradle cache files)
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(path, recursive: true);
            LogMessage?.Invoke($"  Deleted: {path}");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"  [WARN] Could not delete {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static string GradleHome() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gradle");
}