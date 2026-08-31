using System.IO;
using System.Text.RegularExpressions;

namespace AndroidCommandCenter.Services;

public sealed record VariantResolution(
    string Configured, string Resolved, IReadOnlyList<string> Available, string Reason);

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

    /// <summary>
    /// Resolves a CI gate variant after the staging branch has been checked
    /// out. A bare build type such as Release is qualified with the matching
    /// branch flavor (staging + Release => StagingRelease) when flavors exist.
    /// </summary>
    public VariantResolution ResolveGateVariant(
        string projectPath, string configuredVariant, string stagingBranch)
    {
        string configured = configuredVariant.Trim();
        var available = DetectVariants(projectPath);
        if (available.Count == 0)
            throw new InvalidOperationException("No Android application variants were detected.");

        string? exact = available.FirstOrDefault(v =>
            v.Equals(configured, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return new(configured, exact, available, "Configured variant exists.");

        var buildTypeMatches = available
            .Where(v => v.EndsWith(configured, StringComparison.OrdinalIgnoreCase))
            .ToList();
        string branchPart = stagingBranch
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault() ?? stagingBranch;
        branchPart = new string(branchPart.Where(char.IsLetterOrDigit).ToArray());

        var branchMatches = buildTypeMatches
            .Where(v => v.StartsWith(branchPart, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (branchMatches.Count == 1)
            return new(configured, branchMatches[0], available,
                $"Matched the '{stagingBranch}' branch flavor.");

        if (buildTypeMatches.Count == 1)
            return new(configured, buildTypeMatches[0], available,
                "Only one variant uses the configured build type.");

        string choices = string.Join(", ", available);
        throw new InvalidOperationException(
            $"Gate variant '{configured}' is not an exact project variant and could not be " +
            $"resolved for branch '{stagingBranch}'. Available variants: {choices}.");
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
        return ParseDirectNamedBlocks(block);
    }

    private List<string> ParseBuildTypes(string content)
    {
        var block = ExtractBlock(content, "buildTypes");
        var results = block is null ? [] : ParseDirectNamedBlocks(block);

        if (!results.Contains("debug")) results.Insert(0, "debug");
        if (!results.Contains("release")) results.Add("release");
        return results.Distinct().ToList();
    }

    /// <summary>
    /// Finds only immediate child blocks. The earlier regex also mistook
    /// nested blocks such as ndk and firebaseCrashlytics for build types.
    /// </summary>
    private static List<string> ParseDirectNamedBlocks(string block)
    {
        var results = new List<string>();
        int depth = 0;
        foreach (string line in block.Split('\n'))
        {
            if (depth == 0)
            {
                var factory = Regex.Match(line,
                    @"^\s*(?:getByName|create)\s*\(\s*[""'](?<name>\w+)[""']\s*\)\s*\{");
                var named = Regex.Match(line, @"^\s*(?<name>\w+)\s*\{");
                string name = factory.Success ? factory.Groups["name"].Value
                    : named.Success ? named.Groups["name"].Value : "";
                if (name.Length > 0 && !IsGradleKeyword(name) && !results.Contains(name))
                    results.Add(name);
            }
            depth += BraceDelta(line);
        }
        return results;
    }

    private static int BraceDelta(string line)
    {
        int delta = 0;
        char quote = '\0';
        bool escaped = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0')
            {
                if (escaped) { escaped = false; continue; }
                if (c == '\\') { escaped = true; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            if (c == '{') delta++;
            else if (c == '}') delta--;
        }
        return delta;
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
