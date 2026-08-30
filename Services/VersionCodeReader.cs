using System.IO;
using System.Text.RegularExpressions;

namespace AndroidCommandCenter.Services;

public sealed record AppVersion(int? Code, string? Name, string Source);

/// <summary>
/// Reads versionCode/versionName from the app module's Gradle file. Play Store
/// rejects a duplicate versionCode and you can never reuse one, so the CI gate
/// that compares against the last release tag prevents a genuinely
/// unrecoverable mistake.
/// </summary>
public static class VersionCodeReader
{
    public static AppVersion Read(string repoPath)
    {
        foreach (var candidate in Candidates(repoPath))
        {
            if (!File.Exists(candidate)) continue;
            string text;
            try { text = File.ReadAllText(candidate); } catch { continue; }

            int? code = MatchInt(text,
                @"versionCode\s*=\s*(\d+)",          // Kotlin DSL
                @"versionCode\s+(\d+)");             // Groovy

            string? name = MatchString(text,
                @"versionName\s*=\s*[""']([^""']+)[""']",
                @"versionName\s+[""']([^""']+)[""']");

            if (code != null || name != null)
                return new AppVersion(code, name, candidate);

            // Version catalogs / gradle.properties indirection
            var propRef = MatchString(text, @"versionCode\s*=\s*(\w+)\.toInt\(\)",
                                            @"versionCode\s+(\w+)");
            if (propRef != null)
            {
                var fromProps = FromProperties(repoPath, propRef);
                if (fromProps != null) return fromProps with { Source = candidate + " + gradle.properties" };
            }
        }
        return new AppVersion(null, null, "(not found)");
    }

    private static IEnumerable<string> Candidates(string repoPath)
    {
        foreach (var mod in new[] { "app", "mobile", "phone" })
            foreach (var f in new[] { "build.gradle.kts", "build.gradle" })
                yield return Path.Combine(repoPath, mod, f);
    }

    private static AppVersion? FromProperties(string repoPath, string key)
    {
        string props = Path.Combine(repoPath, "gradle.properties");
        if (!File.Exists(props)) return null;
        foreach (var line in File.ReadLines(props))
        {
            var t = line.Trim();
            if (t.StartsWith('#') || !t.StartsWith(key)) continue;
            int eq = t.IndexOf('=');
            if (eq > 0 && int.TryParse(t[(eq + 1)..].Trim(), out var v))
                return new AppVersion(v, null, props);
        }
        return null;
    }

    private static int? MatchInt(string text, params string[] patterns)
    {
        foreach (var p in patterns)
            if (Regex.Match(text, p) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var v))
                return v;
        return null;
    }

    private static string? MatchString(string text, params string[] patterns)
    {
        foreach (var p in patterns)
            if (Regex.Match(text, p) is { Success: true } m)
                return m.Groups[1].Value;
        return null;
    }

    /// <summary>Parses a versionCode out of a release tag like "v1.4.2+312".</summary>
    public static int? FromTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var m = Regex.Match(tag, @"\+(\d+)$");
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }
}