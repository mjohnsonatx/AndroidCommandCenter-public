using System;
using System.IO;

namespace AndroidCommandCenter;

public static class Paths
{
    private const string AppName = "AndroidCommandCenter";

    private static string Base(string envVar, string fallback) =>
        Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } v
            ? v
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                fallback);

    /// <summary>~/.config/AndroidCommandCenter — settings, profiles.</summary>
    public static string ConfigDir =>
        Ensure(Path.Combine(Base("XDG_CONFIG_HOME", ".config"), AppName));

    /// <summary>~/.local/state/AndroidCommandCenter — logs, history, crash.log.</summary>
    public static string StateDir =>
        Ensure(Path.Combine(Base("XDG_STATE_HOME", Path.Combine(".local", "state")), AppName));

    /// <summary>~/.cache/AndroidCommandCenter — disposable.</summary>
    public static string CacheDir =>
        Ensure(Path.Combine(Base("XDG_CACHE_HOME", ".cache"), AppName));

    public static string SettingsFile   => Path.Combine(ConfigDir, "settings.json");
    public static string ProfilesFile   => Path.Combine(ConfigDir, "project_profiles.json");
    public static string EnvFile        => Path.Combine(ConfigDir, "secrets.env");
    public static string PipelineLogs   => Ensure(Path.Combine(StateDir, "pipeline-logs"));
    public static string TestReports    => Ensure(Path.Combine(StateDir, "test-reports"));
    public static string CiRuns         => Ensure(Path.Combine(StateDir, "ci-runs"));
    public static string GradleHome =>
        Environment.GetEnvironmentVariable("GRADLE_USER_HOME") is { Length: > 0 } g
            ? g
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gradle");

    private static string Ensure(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    /// <summary>
    /// One-time move of legacy files out of the build output directory.
    /// Safe to call repeatedly; never overwrites an existing target.
    /// </summary>
    public static void MigrateLegacy(Action<string>? log = null)
    {
        string old = AppDomain.CurrentDomain.BaseDirectory;
        void Move(string from, string to)
        {
            try
            {
                if (File.Exists(from) && !File.Exists(to))
                {
                    File.Move(from, to);
                    log?.Invoke($"[migrate] {Path.GetFileName(from)} -> {to}");
                }
            }
            catch (Exception ex) { log?.Invoke($"[migrate] failed: {ex.Message}"); }
        }
        Move(Path.Combine(old, "acc_settings.json"), SettingsFile);
        Move(Path.Combine(old, ".env"), EnvFile);

        // Old profile/history location was %APPDATA% — on Linux that maps to
        // ~/.config, so check the literal old folder name too.
        string legacyAppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        Move(Path.Combine(legacyAppData, "project_profiles.json"), ProfilesFile);
        Move(Path.Combine(legacyAppData, "deployment_history.json"),
             Path.Combine(StateDir, "deployment_history.json"));
    }
}