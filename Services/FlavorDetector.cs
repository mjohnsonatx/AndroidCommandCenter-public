using System.IO;
using System.Text.RegularExpressions;

namespace AndroidCommandCenter.Services;

public class FlavorDetector
{
    public event Action<string>? LogMessage;

    /// <summary>
    /// Returns variant names like "Debug", "FreeDebug", "PaidRelease"
    /// ready to drop into Gradle task names (assemble{Variant}).
    /// </summary>
    public List<string> DetectVariants(string projectPath)
    {
        string? buildFile = FindBuildFile(projectPath);
        if (buildFile == null)
        {
            LogMessage?.Invoke("No app build.gradle(.kts) found. Using defaults.");
            return ["Debug", "Release"];
        }

        LogMessage?.Invoke($"Scanning: {buildFile}");
        string content = File.ReadAllText(buildFile);

        var flavors = ParseFlavors(content);
        var buildTypes = ParseBuildTypes(content);

        LogMessage?.Invoke($"Flavors:     {(flavors.Count > 0 ? string.Join(", ", flavors) : "(none)")}");
        LogMessage?.Invoke($"Build types: {string.Join(", ", buildTypes)}");

        var variants = new List<string>();

        if (flavors.Count == 0)
        {
            variants.AddRange(buildTypes.Select(Cap));
        }
        else
        {
            foreach (var f in flavors)
                foreach (var bt in buildTypes)
                    variants.Add(Cap(f) + Cap(bt));
        }

        LogMessage?.Invoke($"Variants:    {string.Join(", ", variants)}");
        return variants;
    }

    // ── Build file discovery ────────────────────
    private string? FindBuildFile(string projectPath)
    {
        // Common module names
        foreach (var mod in new[] { "app", "mobile", "phone" })
            foreach (var ext in new[] { "build.gradle.kts", "build.gradle" })
            {
                var p = Path.Combine(projectPath, mod, ext);
                if (File.Exists(p)) return p;
            }

        // Fallback: first sub-module with an android block
        try
        {
            foreach (var dir in Directory.GetDirectories(projectPath))
            {
                if (Path.GetFileName(dir).StartsWith(".")) continue;
                foreach (var ext in new[] { "build.gradle.kts", "build.gradle" })
                {
                    var p = Path.Combine(dir, ext);
                    if (File.Exists(p) && File.ReadAllText(p).Contains("android"))
                        return p;
                }
            }
        }
        catch { }
        return null;
    }

    // ── Groovy / Kotlin DSL parsing ─────────────
    private List<string> ParseFlavors(string content)
    {
        var block = ExtractBlock(content, "productFlavors");
        if (block == null) return [];

        var results = new List<string>();

        // Kotlin DSL: create("name")
        foreach (Match m in Regex.Matches(block, @"create\s*\(\s*""(\w+)"""))
            results.Add(m.Groups[1].Value);

        if (results.Count > 0) return results.Distinct().ToList();

        // Groovy DSL: flavorName { ... }
        foreach (Match m in Regex.Matches(block, @"^\s+(\w+)\s*\{", RegexOptions.Multiline))
        {
            var n = m.Groups[1].Value;
            if (!IsGradleKeyword(n)) results.Add(n);
        }
        return results.Distinct().ToList();
    }

    private List<string> ParseBuildTypes(string content)
    {
        var results = new List<string>();
        var block = ExtractBlock(content, "buildTypes");

        if (block != null)
        {
            foreach (Match m in Regex.Matches(block, @"(?:getByName|create)\s*\(\s*""(\w+)"""))
                results.Add(m.Groups[1].Value);

            foreach (Match m in Regex.Matches(block, @"^\s+(\w+)\s*\{", RegexOptions.Multiline))
            {
                var n = m.Groups[1].Value;
                if (!IsGradleKeyword(n) && !results.Contains(n)) results.Add(n);
            }
        }

        if (!results.Contains("debug")) results.Insert(0, "debug");
        if (!results.Contains("release")) results.Add("release");
        return results.Distinct().ToList();
    }

    // ── Helpers ─────────────────────────────────
    private static string? ExtractBlock(string content, string name)
    {
        int idx = content.IndexOf(name);
        if (idx < 0) return null;
        int brace = content.IndexOf('{', idx);
        if (brace < 0) return null;

        int depth = 1, pos = brace + 1;
        while (pos < content.Length && depth > 0)
        {
            if (content[pos] == '{') depth++;
            else if (content[pos] == '}') depth--;
            pos++;
        }
        return content.Substring(brace + 1, pos - brace - 2);
    }

    private static bool IsGradleKeyword(string n) =>
        n is "create" or "register" or "getByName" or "named"
            or "all" or "forEach" or "configureEach" or "withType" or "initWith";

    private static string Cap(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];
}