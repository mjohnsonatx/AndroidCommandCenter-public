using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AndroidCommandCenter.Services;

public sealed record ChecksumIssue(string Severity, string Message);

/// <summary>
/// Supply-chain integrity checks on the build toolchain. These catch a
/// compromised or silently-swapped Gradle wrapper, which is the highest-value
/// attack against an Android release pipeline, plus the common own-goal of an
/// unpinned wrapper distribution.
/// </summary>
public static class ChecksumValidator
{
    public static async Task<List<ChecksumIssue>> ValidateAsync(
        string repoPath, CancellationToken ct)
    {
        var issues = new List<ChecksumIssue>();

        // ── 1. Wrapper properties must pin a distribution SHA-256 ──────────
        string propsPath = Path.Combine(repoPath, "gradle", "wrapper", "gradle-wrapper.properties");
        if (!File.Exists(propsPath))
        {
            issues.Add(new("Error", "gradle/wrapper/gradle-wrapper.properties is missing."));
            return issues;
        }

        string props = await File.ReadAllTextAsync(propsPath, ct);

        if (!Regex.IsMatch(props, @"distributionSha256Sum\s*=\s*[0-9a-fA-F]{64}"))
            issues.Add(new("Warning",
                "gradle-wrapper.properties has no distributionSha256Sum. The Gradle distribution " +
                "is downloaded unverified. Add it with: ./gradlew wrapper --gradle-version <v> " +
                "--gradle-distribution-sha256-sum <sha>"));

        var distUrl = Regex.Match(props, @"distributionUrl\s*=\s*(\S+)");
        if (distUrl.Success)
        {
            string url = distUrl.Groups[1].Value.Replace("\\:", ":");
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                issues.Add(new("Error", $"Gradle distribution is fetched over plaintext HTTP: {url}"));
            else if (!url.Contains("services.gradle.org", StringComparison.OrdinalIgnoreCase) &&
                     !url.Contains("gradle.org", StringComparison.OrdinalIgnoreCase))
                issues.Add(new("Warning", $"Gradle distribution comes from a non-official host: {url}"));
        }

        // ── 2. The wrapper JAR must match what git has ────────────────────
        // A modified gradle-wrapper.jar executes arbitrary code on every build.
        string jar = Path.Combine(repoPath, "gradle", "wrapper", "gradle-wrapper.jar");
        if (!File.Exists(jar))
            issues.Add(new("Error", "gradle/wrapper/gradle-wrapper.jar is missing."));
        else
        {
            var fi = new FileInfo(jar);
            // The real wrapper jar is ~43-64 KB. Anything much larger is suspect.
            if (fi.Length > 200_000)
                issues.Add(new("Error",
                    $"gradle-wrapper.jar is {fi.Length:N0} bytes — far larger than expected (~60 KB). " +
                    "Treat as compromised until verified."));
            await using var fs = File.OpenRead(jar);
            string sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            issues.Add(new("Info", $"gradle-wrapper.jar SHA-256 = {sha}"));
        }

        // ── 3. Dependency verification / lockfiles ────────────────────────
        bool hasVerification = File.Exists(Path.Combine(repoPath, "gradle", "verification-metadata.xml"));
        if (!hasVerification)
            issues.Add(new("Warning",
                "No gradle/verification-metadata.xml — dependency checksums are not verified. " +
                "Generate with: ./gradlew --write-verification-metadata sha256 help"));

        // ── 4. Repositories must not be plaintext ─────────────────────────
        foreach (var gradleFile in EnumerateGradleFiles(repoPath))
        {
            string text;
            try { text = await File.ReadAllTextAsync(gradleFile, ct); } catch { continue; }
            foreach (Match m in Regex.Matches(text, @"url\s*[=(]\s*[""'](http://[^""']+)[""']"))
                issues.Add(new("Error",
                    $"Plaintext HTTP repository in {Path.GetRelativePath(repoPath, gradleFile)}: " +
                    m.Groups[1].Value));
            if (Regex.IsMatch(text, @"mavenLocal\s*\(\s*\)"))
                issues.Add(new("Warning",
                    $"mavenLocal() in {Path.GetRelativePath(repoPath, gradleFile)} — " +
                    "release builds should not resolve from the local cache."));
        }

        return issues;
    }

    private static IEnumerable<string> EnumerateGradleFiles(string repoPath)
    {
        foreach (var name in new[]
        {
            "build.gradle", "build.gradle.kts",
            "settings.gradle", "settings.gradle.kts"
        })
        {
            string p = Path.Combine(repoPath, name);
            if (File.Exists(p)) yield return p;
        }
        if (!Directory.Exists(repoPath)) yield break;
        foreach (var dir in Directory.GetDirectories(repoPath))
        {
            if (Path.GetFileName(dir).StartsWith('.')) continue;
            foreach (var name in new[] { "build.gradle", "build.gradle.kts" })
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) yield return p;
            }
        }
    }
}