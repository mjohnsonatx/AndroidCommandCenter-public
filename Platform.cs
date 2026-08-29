using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace AndroidCommandCenter;

public static class Platform
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string Exe(string name) => IsWindows ? name + ".exe" : name;

    /// <summary>gradlew wrapper for a project, or null if missing.</summary>
    public static string? GradleWrapper(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return null;
        string p = Path.Combine(projectPath, IsWindows ? "gradlew.bat" : "gradlew");
        return File.Exists(p) ? p : null;
    }

    /// <summary>
    /// Ensures ./gradlew is executable. Git preserves the exec bit, but files
    /// copied from a Windows checkout or extracted from a zip often lose it,
    /// producing a bare "Permission denied" that looks like a Gradle bug.
    /// </summary>
    public static void EnsureExecutable(string path)
    {
        if (IsWindows || !File.Exists(path)) return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            var want = mode | UnixFileMode.UserExecute
                            | UnixFileMode.GroupExecute
                            | UnixFileMode.OtherExecute;
            if (mode != want) File.SetUnixFileMode(path, want);
        }
        catch { /* best effort */ }
    }

    /// <summary>Resolve an SDK tool; falls back to PATH lookup.</summary>
    public static string ResolveSdkTool(string sdkPath, string subDir, string tool)
    {
        string candidate = Path.Combine(sdkPath, subDir, Exe(tool));
        if (File.Exists(candidate)) return candidate;
        return Which(tool) ?? candidate; // return candidate so error names the path
    }

    /// <summary>`which` equivalent.</summary>
    public static string? Which(string tool)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string p = Path.Combine(dir.Trim(), Exe(tool));
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    public static string ResolveAndroidSdk()
    {
        foreach (var v in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } p && Directory.Exists(p))
                return p;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var c in new[]
        {
            Path.Combine(home, "Android", "Sdk"),
            Path.Combine(home, "Android", "sdk"),
            "/opt/android-sdk",
            "/usr/lib/android-sdk",
        })
            if (Directory.Exists(c)) return c;

        return Path.Combine(home, "Android", "Sdk"); // non-existent; caller reports
    }

    /// <summary>Open a directory in the file manager.</summary>
    public static void OpenDirectory(string dir)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir);
        Launch(IsWindows ? "explorer.exe" : "xdg-open", dir);
    }

    /// <summary>
    /// Reveal a file, selecting it if the desktop supports it. Falls back to
    /// opening the containing folder — there is no portable "select file" on Linux.
    /// </summary>
    public static void RevealFile(string file)
    {
        if (!File.Exists(file)) throw new FileNotFoundException(file);
        if (IsWindows) { Launch("explorer.exe", $"/select,\"{file}\""); return; }

        // Try the FileManager1 D-Bus interface (Nautilus, Dolphin, Nemo…)
        try
        {
            var uri = new Uri(file).AbsoluteUri;
            var psi = new ProcessStartInfo("gdbus")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in new[]
            {
                "call", "--session",
                "--dest", "org.freedesktop.FileManager1",
                "--object-path", "/org/freedesktop/FileManager1",
                "--method", "org.freedesktop.FileManager1.ShowItems",
                $"['{uri}']", "''"
            }) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p != null && p.WaitForExit(2000) && p.ExitCode == 0) return;
        }
        catch { }

        OpenDirectory(Path.GetDirectoryName(file)!);
    }

    /// <summary>Open a text file in the user's editor/viewer.</summary>
    public static void OpenFile(string file)
    {
        if (!File.Exists(file)) throw new FileNotFoundException(file);
        Launch(IsWindows ? "explorer.exe" : "xdg-open", file);
    }

    private static void Launch(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }
}