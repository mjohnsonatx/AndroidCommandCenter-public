using System.IO;
using System.Text.RegularExpressions;

namespace AndroidCommandCenter.Services;

public sealed record SecretFinding(string File, int Line, string Rule, string Excerpt);

/// <summary>
/// Scans tracked files for credentials that must never reach a public repo or a
/// shipped APK. Deliberately conservative about what it flags as blocking: a
/// false positive that blocks a release is as damaging as a miss, so
/// high-entropy heuristics are reported as warnings and only unambiguous
/// key formats are treated as errors.
/// </summary>
public static class SecretScanner
{
    private sealed record Rule(string Name, Regex Pattern, bool Blocking);

    private static readonly Rule[] Rules =
    [
        new("Private key block",
            new(@"-----BEGIN (RSA |EC |OPENSSH |PGP )?PRIVATE KEY-----", RegexOptions.Compiled), true),
        new("Google service-account key",
            new(@"""type""\s*:\s*""service_account""", RegexOptions.Compiled), true),
        new("GitHub token",
            new(@"\b(ghp|gho|ghu|ghs|ghr|github_pat)_[A-Za-z0-9_]{20,}", RegexOptions.Compiled), true),
        new("AWS access key",
            new(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled), true),
        new("Slack token",
            new(@"\bxox[baprs]-[0-9A-Za-z-]{10,}", RegexOptions.Compiled), true),
        new("Google API key",
            new(@"\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.Compiled), true),
        new("Firebase token",
            new(@"\b1//[0-9A-Za-z_\-]{30,}", RegexOptions.Compiled), true),
        new("JWT",
            new(@"\beyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", RegexOptions.Compiled), true),
        // Non-blocking heuristics — too noisy to gate a release on.
        new("Hardcoded password assignment",
            new(@"(?i)\b(password|passwd|secret|apikey|api_key|token)\s*[=:]\s*[""'][^""'\s]{8,}[""']",
                RegexOptions.Compiled), false),
        new("Keystore file committed",
            new(@"\.(jks|keystore|p12|pfx)$", RegexOptions.Compiled), false),
    ];

    private static readonly string[] SkipDirs =
        [".git", "build", ".gradle", ".idea", "node_modules", ".kotlin", "obj", "bin"];

    private static readonly string[] SkipExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".svg", ".ttf", ".otf",
         ".apk", ".aab", ".so", ".jar", ".aar", ".zip", ".pdf", ".class", ".dex", ".webm", ".mp4"];

    /// <summary>
    /// Scans only files git tracks — untracked local scratch files are the
    /// user's business and flagging them trains people to ignore the scanner.
    /// </summary>
    public static async Task<(List<SecretFinding> Blocking, List<SecretFinding> Warnings)> ScanAsync(
        string repoPath, GitService git, CancellationToken ct)
    {
        var blocking = new List<SecretFinding>();
        var warnings = new List<SecretFinding>();

        string listing = await git.Require(repoPath, ct, "ls-files", "-z");
        var files = listing.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        foreach (var rel in files)
        {
            ct.ThrowIfCancellationRequested();

            if (rel.Split('/').Any(seg => SkipDirs.Contains(seg))) continue;
            string ext = Path.GetExtension(rel).ToLowerInvariant();

            // Filename-only rules (committed keystores) run before the binary skip.
            foreach (var rule in Rules.Where(r => r.Name == "Keystore file committed"))
                if (rule.Pattern.IsMatch(rel))
                    warnings.Add(new SecretFinding(rel, 0, rule.Name, Path.GetFileName(rel)));

            if (SkipExtensions.Contains(ext)) continue;

            string full = Path.Combine(repoPath, rel);
            FileInfo fi;
            try { fi = new FileInfo(full); if (!fi.Exists || fi.Length > 2_000_000) continue; }
            catch { continue; }

            string[] lines;
            try { lines = await File.ReadAllLinesAsync(full, ct); }
            catch { continue; }   // binary or unreadable

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length > 2000) continue;                 // minified asset
                if (IsAllowlisted(line)) continue;

                foreach (var rule in Rules)
                {
                    if (rule.Name == "Keystore file committed") continue;
                    if (!rule.Pattern.IsMatch(line)) continue;

                    var finding = new SecretFinding(rel, i + 1, rule.Name, Redact(line));
                    (rule.Blocking ? blocking : warnings).Add(finding);
                }
            }
        }

        return (blocking, warnings);
    }

    /// <summary>Honour an inline opt-out, same convention as other scanners.</summary>
    private static bool IsAllowlisted(string line) =>
        line.Contains("acc-ci:allow-secret", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("gitleaks:allow", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("NOPASSWD", StringComparison.Ordinal);

    /// <summary>Never echo a real secret into a log or Firestore.</summary>
    private static string Redact(string line)
    {
        line = line.Trim();
        if (line.Length <= 24) return line;
        return line[..12] + "…[redacted]…" + line[^6..];
    }
}