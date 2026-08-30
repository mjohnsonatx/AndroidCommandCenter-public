using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AndroidCommandCenter.Services;

namespace AndroidCommandCenter.Ci;

// ─────────────────────────────────────────────────────────────────────────────
// 1. PREFLIGHT — fail in two seconds for things that would fail in twenty minutes
// ─────────────────────────────────────────────────────────────────────────────
public sealed class PreflightStage : ICiStage
{
    public string Name => "Preflight";

    public async Task RunAsync(CiContext ctx, StageRecord rec, StageOutput _, CancellationToken ct)
    {
        var problems = new List<string>();

        if (!await ctx.Git.IsRepo(ctx.Repo, ct))
            problems.Add($"'{ctx.Repo}' is not a git repository.");
        if (Platform.GradleWrapper(ctx.ProjectPath) is null)
            problems.Add("No gradlew in project root.");
        if (Platform.Which("git") is null) problems.Add("git not on PATH.");
        if (Platform.Which("java") is null) problems.Add("java not on PATH.");

        foreach (var b in new[] { ctx.Dev, ctx.Staging, ctx.Main })
            if (!Models.GitNames.IsValid(b)) problems.Add($"Invalid branch name '{b}'.");
        if (!Models.GitNames.IsValid(ctx.Remote)) problems.Add($"Invalid remote '{ctx.Remote}'.");

        if (problems.Count == 0 && await ctx.Git.IsDirty(ctx.Repo, ct))
            problems.Add("Working tree has uncommitted changes:\n" + await ctx.Git.StatusSummary(ctx.Repo, ct));

        if (ctx.Config.PushMain && string.IsNullOrWhiteSpace(ctx.GitHubToken))
        {
            // Token-less pushes work only if the remote uses SSH or a cached helper.
            var url = (await ctx.Git.Run(ctx.Repo, ct, "remote", "get-url", ctx.Remote)).Output;
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                problems.Add("Pushing over HTTPS requires a GitHub token (none stored for this project).");
        }

        rec.Data["dev"] = ctx.Dev; rec.Data["staging"] = ctx.Staging; rec.Data["main"] = ctx.Main;
        if (problems.Count > 0)
            throw new StageFailedException(string.Join("\n", problems));

        rec.Summary = "Environment and repository OK.";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. SYNC — fetch everything, hard-sync staging to remote, snapshot SHAs
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SyncStage : ICiStage
{
    public string Name => "Sync branches";

    public async Task RunAsync(CiContext ctx, StageRecord rec, StageOutput _, CancellationToken ct)
    {
        var fetch = await ctx.Git.Fetch(ctx.Repo, ctx.Remote, ct);
        if (!fetch.Ok) throw new StageFailedException($"git fetch failed:\n{fetch.Output}");

        foreach (var b in new[] { ctx.Staging, ctx.Main, ctx.Dev })
            if (!await ctx.Git.BranchExistsRemote(ctx.Repo, ctx.Remote, b, ct))
                throw new StageFailedException($"Branch '{b}' does not exist on '{ctx.Remote}'.");

        var reset = await ctx.Git.ResetToRemote(ctx.Repo, ctx.Remote, ctx.Staging, ct);
        if (!reset.Ok) throw new StageFailedException($"Could not sync {ctx.Staging}:\n{reset.Output}");

        var m = ctx.Manifest;
        m.StagingSha = await ctx.Git.RevParse(ctx.Repo, "HEAD", ct);
        m.MainShaBefore = await ctx.Git.RevParse(ctx.Repo, $"{ctx.Remote}/{ctx.Main}", ct);

        // What are we about to promote?
        var raw = await ctx.Git.Require(ctx.Repo, ct, "log", "--no-merges",
            "--pretty=format:%H%x1f%an%x1f%s", $"{ctx.Remote}/{ctx.Main}..HEAD");
        m.Commits = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\x1f'))
            .Where(p => p.Length == 3)
            .Select(p => new CommitInfo { Sha = p[0], Author = p[1], Subject = p[2] })
            .ToList();

        if (m.Commits.Count == 0)
        {
            // Not an error — but there is nothing to promote. Mark and let the
            // runner short-circuit with a clean "nothing to do".
            rec.Status = StageStatus.Warning;
            rec.Summary = $"{ctx.Staging} has no commits beyond {ctx.Main}. Nothing to promote.";
            rec.Data["nothingToPromote"] = true;
            return;
        }

        // Staging must contain main — otherwise main has hotfixes that were never
        // back-merged and the promotion would silently drop them.
        var behind = await ctx.Git.CountBetween(ctx.Repo, "HEAD", $"{ctx.Remote}/{ctx.Main}", ct);
        if (behind > 0)
            throw new StageFailedException(
                $"{ctx.Main} has {behind} commit(s) that are not in {ctx.Staging}. " +
                $"Back-merge {ctx.Main} into {ctx.Dev} and {ctx.Staging} first, or the promotion would discard them.");

        rec.Summary = $"{m.Commits.Count} commit(s) to promote. staging={m.StagingSha[..8]} main={m.MainShaBefore[..8]}";
        rec.Data["commitCount"] = m.Commits.Count;
        foreach (var c in m.Commits.Take(20)) ctx.Info($"  {c.Sha[..8]}  {c.Author,-18} {c.Subject}");
        if (m.Commits.Count > 20) ctx.Info($"  …and {m.Commits.Count - 20} more");
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. VERSION CODE GATE — Play Store rejects a duplicate versionCode, and you
//    can never re-upload an old one, so this is the one mistake you can't undo.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class VersionCodeGate : ICiStage
{
    public string Name => "Version code";
    public bool ShouldRun(CiContext ctx) => ctx.Config.RequireVersionCodeBump;

    private static readonly Regex VersionCodeRx = new(@"versionCode\s*[=(]?\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex VersionNameRx = new(@"versionName\s*[=(]?\s*[""']([^""']+)[""']", RegexOptions.Compiled);

    public async Task RunAsync(CiContext ctx, StageRecord rec, StageOutput _, CancellationToken ct)
    {
        string rel = FindBuildFile(ctx.ProjectPath)
            ?? throw new StageFailedException("Could not find app/build.gradle(.kts) to read versionCode.");

        string stagingText = await File.ReadAllTextAsync(Path.Combine(ctx.ProjectPath, rel), ct);
        var mainShow = await ctx.Git.Run(ctx.Repo, ct, "show", $"{ctx.Remote}/{ctx.Main}:{rel}");
        string mainText = mainShow.Ok ? mainShow.Output : "";

        int? staging = Parse(stagingText, VersionCodeRx);
        int? main = Parse(mainText, VersionCodeRx);
        string? name = VersionNameRx.Match(stagingText) is { Success: true } nm ? nm.Groups[1].Value : null;

        rec.Data["stagingVersionCode"] = staging;
        rec.Data["mainVersionCode"] = main;
        rec.Data["versionName"] = name;
        ctx.Manifest.VersionCode = staging;
        ctx.Manifest.VersionName = name;

        if (staging is null)
        {
            // versionCode may come from a property or a version catalog; warn rather than block.
            rec.Status = StageStatus.Warning;
            rec.Summary = $"Could not parse a literal versionCode from {rel}; skipping bump check.";
            return;
        }
        if (main is not null && staging <= main)
            throw new StageFailedException(
                $"versionCode on {ctx.Staging} is {staging} but {ctx.Main} already has {main}. " +
                "Play Store requires a strictly higher versionCode for every upload.");

        rec.Summary = $"versionCode {main?.ToString() ?? "?"} → {staging}" + (name != null ? $" ({name})" : "");
    }

    private static int? Parse(string text, Regex rx) =>
        rx.Match(text) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var v) ? v : null;

    internal static string? FindBuildFile(string project)
    {
        foreach (var mod in new[] { "app", "mobile" })
            foreach (var f in new[] { "build.gradle.kts", "build.gradle" })
                if (File.Exists(Path.Combine(project, mod, f))) return $"{mod}/{f}";
        return null;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. SECRET SCAN — the diff main..staging, plus tracked-file hygiene
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SecretScanStage : ICiStage
{
    public string Name => "Secret scan";
    public bool ShouldRun(CiContext ctx) => ctx.Config.RunSecretScan;

    private static readonly (string Id, Regex Rx)[] Patterns =
    [
        ("private-key",      new(@"-----BEGIN (RSA |EC |DSA |OPENSSH |PGP )?PRIVATE KEY-----", RegexOptions.Compiled)),
        ("google-api-key",   new(@"AIza[0-9A-Za-z\-_]{35}", RegexOptions.Compiled)),
        ("aws-access-key",   new(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        ("github-token",     new(@"\b(ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,}\b", RegexOptions.Compiled)),
        ("github-pat",       new(@"github_pat_[A-Za-z0-9_]{80,}", RegexOptions.Compiled)),
        ("slack-token",      new(@"xox[baprs]-[0-9A-Za-z\-]{10,}", RegexOptions.Compiled)),
        ("firebase-sa-json", new(@"""type""\s*:\s*""service_account""", RegexOptions.Compiled)),
        ("openai-key",       new(@"\bsk-[A-Za-z0-9]{20,}T3BlbkFJ[A-Za-z0-9]{20,}\b", RegexOptions.Compiled)),
        ("generic-secret",   new(@"(?i)(password|passwd|secret|api[_-]?key|storePassword|keyPassword)\s*[:=]\s*[""'][^""'\s]{8,}[""']", RegexOptions.Compiled)),
        ("jwt",              new(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.Compiled)),
    ];

    private static readonly string[] SensitiveNames =
        [".jks", ".keystore", ".p12", ".pem", ".key", "google-services.json", "local.properties", ".env", "secrets.properties"];

    // Test fixtures and docs generate false positives.
    private static readonly Regex IgnorePath = new(@"(^|/)(test|androidTest|docs?|samples?)/|\.md$|\.lock$", RegexOptions.Compiled);

    public async Task RunAsync(CiContext ctx, StageRecord rec, StageOutput _, CancellationToken ct)
    {
        var changed = (await ctx.Git.Require(ctx.Repo, ct, "diff", "--name-only", "--diff-filter=AM",
                $"{ctx.Remote}/{ctx.Main}...HEAD"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

        var findings = new List<string>();
        var warnings = new List<string>();

        // Sensitive filenames added anywhere in the diff are a hard failure.
        foreach (var f in changed)
            if (SensitiveNames.Any(n => f.EndsWith(n, StringComparison.OrdinalIgnoreCase)))
                findings.Add($"{f}: sensitive file committed");

        foreach (var rel in changed.Where(f => !IgnorePath.IsMatch(f)))
        {
            string full = Path.Combine(ctx.ProjectPath, rel);
            if (!File.Exists(full) || new FileInfo(full).Length > 2_000_000) continue;
            if (IsBinary(full)) continue;

            int ln = 0;
            foreach (var line in await File.ReadAllLinesAsync(full, ct))
            {
                ln++;
                foreach (var (id, rx) in Patterns)
                {
                    if (!rx.IsMatch(line)) continue;
                    // generic-secret is noisy: a BuildConfig placeholder or "changeme" is fine.
                    if (id == "generic-secret" && Regex.IsMatch(line, @"(?i)(example|changeme|placeholder|your[_-]|\$\{|BuildConfig\.|System\.getenv|project\.property)"))
                        continue;
                    string target = id == "generic-secret" ? "warn" : "fail";
                    string msg = $"{rel}:{ln}: {id}";
                    (target == "fail" ? findings : warnings).Add(msg);
                }
            }
        }

        // Hygiene: keystores must be gitignored.
        string gi = Path.Combine(ctx.ProjectPath, ".gitignore");
        if (File.Exists(gi))
        {
            var text = await File.ReadAllTextAsync(gi, ct);
            if (!text.Contains(".jks") && !text.Contains(".keystore"))
                warnings.Add(".gitignore does not exclude *.jks / *.keystore");
            if (!text.Contains("local.properties"))
                warnings.Add(".gitignore does not exclude local.properties");
        }

        rec.Data["filesScanned"] = changed.Count;
        rec.Data["findings"] = findings;
        rec.Data["warnings"] = warnings;
        foreach (var w in warnings) ctx.Info($"  [warn] {w}");
        foreach (var f in findings) ctx.Info($"  [FAIL] {f}");

        if (findings.Count > 0)
            throw new StageFailedException($"{findings.Count} secret(s) detected in the diff:\n" + string.Join("\n", findings));

        rec.Status = warnings.Count > 0 ? StageStatus.Warning : StageStatus.Passed;
        rec.Summary = $"{changed.Count} changed file(s) scanned, 0 secrets, {warnings.Count} warning(s).";
    }

    private static bool IsBinary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[8000];
            int n = fs.Read(buf, 0, buf.Length);
            return buf.Take(n).Any(b => b == 0);
        }
        catch { return true; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 5. CHECKSUMS — gradle wrapper integrity + distribution pinning + APK hash
// ─────────────────────────────────────────────────────────────────────────────
public sealed class ChecksumStage : ICiStage
{
    public string Name => "Checksum validation";
    public bool ShouldRun(CiContext ctx) => ctx.Config.RunChecksumValidation;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task RunAsync(CiContext ctx, StageRecord rec, StageOutput _, CancellationToken ct)
    {
        var warnings = new List<string>();
        string wrapperDir = Path.Combine(ctx.ProjectPath, "gradle", "wrapper");
        string propsPath = Path.Combine(wrapperDir, "gradle-wrapper.properties");
        string jarPath = Path.Combine(wrapperDir, "gradle-wrapper.jar");

        if (!File.Exists(propsPath)) throw new StageFailedException("gradle-wrapper.properties missing.");
        if (!File.Exists(jarPath)) throw new StageFailedException("gradle-wrapper.jar missing — the wrapper is broken.");

        var props = (await File.ReadAllLinesAsync(propsPath, ct))
            .Where(l => l.Contains('=') && !l.TrimStart().StartsWith('#'))
            .Select(l => l.Split('=', 2))
            .ToDictionary(p => p[0].Trim(), p => p[1].Trim().Replace("\\:", ":"));

        string dist = props.GetValueOrDefault("distributionUrl", "");
        var vm = Regex.Match(dist, @"gradle-(\d+(\.\d+)+)-(bin|all)\.zip");
        string version = vm.Success ? vm.Groups[1].Value : "";
        rec.Data["gradleVersion"] = version;

        // distributionSha256Sum pins the Gradle distribution itself. Its absence
        // means a compromised mirror could serve a tampered Gradle.
        if (!props.ContainsKey("distributionSha256Sum"))
            warnings.Add("distributionSha256Sum is not set in gradle-wrapper.properties (Gradle distribution is unpinned).");

        // Verify the wrapper jar against Gradle's published checksum.
        string actual = Sha256(jarPath);
        rec.Data["wrapperJarSha256"] = actual;
        if (version.Length > 0)
        {
            try
            {
                string expected = (await Http.GetStringAsync(
                    $"https://services.gradle.org/distributions/gradle-{version}-wrapper.jar.sha256", ct)).Trim();
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                    throw new StageFailedException(
                        $"gradle-wrapper.jar checksum mismatch for Gradle {version}.\n  expected {expected}\n  actual   {actual}\n" +
                        "The wrapper jar has been modified — do not run it.");
                ctx.Info($"  gradle-wrapper.jar verified against Gradle {version} published checksum.");
            }
            catch (HttpRequestException ex)
            {
                warnings.Add($"Could not fetch published wrapper checksum (offline?): {ex.Message}");
            }
        }

        // Dependency verification: gradle/verification-metadata.xml
        bool depVerify = File.Exists(Path.Combine(ctx.ProjectPath, "gradle", "verification-metadata.xml"));
        rec.Data["dependencyVerification"] = depVerify;
        if (!depVerify)
            warnings.Add("No gradle/verification-metadata.xml — dependency checksums are not verified by Gradle. " +
                         "Consider `./gradlew --write-verification-metadata sha256`.");

        rec.Data["warnings"] = warnings;
        foreach (var w in warnings) ctx.Info($"  [warn] {w}");
        rec.Status = warnings.Count > 0 ? StageStatus.Warning : StageStatus.Passed;
        rec.Summary = $"Wrapper jar OK (Gradle {version}); {warnings.Count} hardening warning(s).";
    }

    internal static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 6. LINT — Android Lint; Error/Fatal fail, Warning configurable
// ─────────────────────────────────────────────────────────────────────────────
public sealed class LintStage : ICiStage
{
    public string Name => "Android Lint";
    public bool ShouldRun(CiContext ctx) => ctx.Config.RunLint;

    public async Task RunAsync(CiContext ctx, StageRecord rec, StageOutput _, CancellationToken ct)
    {
        int code = await ctx.Android.RunLint(ctx.Config.GateVariant, ct);
        var report = LintReportService.Parse(ctx.ProjectPath, ctx.Config.GateVariant);

        rec.ExitCode = code;
        rec.Data["errors"] = report.Errors;
        rec.Data["warnings"] = report.Warnings;
        rec.Data["xml"] = report.XmlPath;
        rec.Data["html"] = report.HtmlPath;
        rec.Data["topIssues"] = report.Issues
            .Where(i => i.Severity is "Fatal" or "Error")
            .Take(25).Select(i => $"{i.Id}: {i.Message} @ {i.Location}").ToList();

        if (report.XmlPath is null)
            throw new StageFailedException($"Lint exited {code} and produced no report. Is the lint task configured for '{ctx.Config.GateVariant}'?");

        ctx.Info("  " + report.Summary);
        if (report.Errors > 0)
            throw new StageFailedException($"Lint: {report.Errors} error(s).\n{report.Detail}");
        if (ctx.Config.LintFailsOnWarning && report.Warnings > 0)
            throw new StageFailedException($"Lint: {report.Warnings} warning(s) and LintFailsOnWarning is enabled.\n{report.Detail}");
        if (code != 0)
            throw new StageFailedException($"Lint task exited {code} despite a clean report (check abortOnError / lintOptions).");

        rec.Status = report.Warnings > 0 ? StageStatus.Warning : StageStatus.Passed;
        rec.Summary = $"0 errors, {report.Warnings} warning(s).";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 7/8. TESTS
// ─────────────────────────────────────────────────────────────────────────────
public sealed class UnitTestStage : ICiStage
{
    public string Name => "Unit tests";
    public bool ShouldRun(CiContext ctx) => ctx.Config.RunUnitTests;

    public async Task RunAsync(CiContext ctx, StageRecord rec, StageOutput _, CancellationToken ct)
    {
        ctx.Android.SelectedVariant = ctx.Config.GateVariant;
        var at = DateTime.Now;
        int code = await ctx.Android.RunUnitTests(ct);
        var rpt = ctx.TestReports.ParseResults(ctx.ProjectPath, "UnitTests", at);

        rec.ExitCode = code;
        rec.Data["total"] = rpt.Total; rec.Data["passed"] = rpt.Passed;
        rec.Data["failed"] = rpt.Failed; rec.Data["skipped"] = rpt.Skipped;
        rec.Data["failures"] = rpt.Failures.Take(50)
            .Select(f => $"{f.ClassName}.{f.Name}: {f.FailureMessage}").ToList();

        if (rpt.Total == 0)
            throw new StageFailedException($"Unit tests produced no results (gradle exit {code}). " +
                                           "Either none exist for this variant or the task failed before running.");
        if (rpt.Failed > 0 || code != 0)
            throw new StageFailedException($"{rpt.Failed} of {rpt.Total} unit tests failed (exit {code}).");

        rec.Summary = $"{rpt.Passed}/{rpt.Total} passed, {rpt.Skipped} skipped.";
    }
}

public sealed class InstrumentTestStage : ICiStage
{
    public string Name => "Instrument tests";
    public bool ShouldRun(CiContext ctx) => ctx.Config.RunInstrumentTests;

    public async Task RunAsync(CiContext ctx, StageRecord rec, StageOutput _, CancellationToken ct)
    {
        ctx.Android.SelectedVariant = ctx.Config.GateVariant;
        var at = DateTime.Now;
        int code = await ctx.Android.RunInstrumentTests(ct);
        var rpt = ctx.TestReports.ParseResults(ctx.ProjectPath, "InstrumentTests", at);

        rec.ExitCode = code;
        rec.Data["total"] = rpt.Total; rec.Data["failed"] = rpt.Failed;
        rec.Data["failures"] = rpt.Failures.Take(50)
            .Select(f => $"{f.ClassName}.{f.Name}: {f.FailureMessage}").ToList();

        if (rpt.Total == 0)
            throw new StageFailedException($"Instrument tests produced no results (exit {code}). Is a device/emulator connected?");
        if (rpt.Failed > 0 || code != 0)
            throw new StageFailedException($"{rpt.Failed} of {rpt.Total} instrument tests failed.");

        rec.Summary = $"{rpt.Passed}/{rpt.Total} passed.";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 9. BUILD — prove the gate variant compiles & packages; record the APK hash
// ─────────────────────────────────────────────────────────────────────────────
public sealed class BuildStage : ICiStage
{
    public string Name => "Build";

    public async Task RunAsync(CiContext ctx, StageRecord rec, StageOutput _, CancellationToken ct)
    {
        ctx.Android.SelectedVariant = ctx.Config.GateVariant;
        int code = await ctx.Android.AssembleVariant(ct);
        rec.ExitCode = code;
        if (code != 0) throw new StageFailedException($"assemble{ctx.Config.GateVariant} exited {code}.");

        string root = Path.Combine(ctx.ProjectPath, "app", "build", "outputs", "apk");
        var apk = Directory.Exists(root)
            ? Directory.GetFiles(root, "*.apk", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
            : null;

        if (apk != null)
        {
            rec.Data["apk"] = apk;
            rec.Data["apkSha256"] = ChecksumStage.Sha256(apk);
            rec.Data["apkBytes"] = new FileInfo(apk).Length;
            rec.Summary = $"{Path.GetFileName(apk)} ({new FileInfo(apk).Length / 1_048_576.0:F1} MB)";
        }
        else rec.Summary = "Build succeeded (no APK located to hash).";
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 10. PROMOTE — main ← staging, --no-ff, tag, push
// ─────────────────────────────────────────────────────────────────────────────
public sealed class PromoteStage : ICiStage
{
    public string Name => "Promote to main";
    public bool ShouldRun(CiContext ctx) => ctx.Config.PromoteToMain;

    public async Task RunAsync(CiContext ctx, StageRecord rec, StageOutput _, CancellationToken ct)
    {
        var m = ctx.Manifest;
        await ctx.Git.EnsureIdentity(ctx.Repo, ct);

        var sync = await ctx.Git.ResetToRemote(ctx.Repo, ctx.Remote, ctx.Main, ct);
        if (!sync.Ok) throw new StageFailedException($"Could not sync {ctx.Main}:\n{sync.Output}");

        // Guard against a race: main moved since Sync.
        string mainNow = await ctx.Git.RevParse(ctx.Repo, "HEAD", ct);
        if (mainNow != m.MainShaBefore)
            throw new StageFailedException(
                $"{ctx.Main} changed during the run ({m.MainShaBefore[..8]} → {mainNow[..8]}). Re-run to re-validate against the new head.");

        string tag = BuildTag(ctx);
        string msg = $"Release {tag}: merge {ctx.Staging} into {ctx.Main}\n\n" +
                     $"CI run {m.RunId}\n{m.Commits.Count} commit(s):\n" +
                     string.Join("\n", m.Commits.Take(50).Select(c => $"- {c.Sha[..8]} {c.Subject}"));

        if (ctx.DryRun)
        {
            rec.Status = StageStatus.Skipped;
            rec.Summary = $"[dry-run] would merge {ctx.Staging}@{m.StagingSha[..8]} into {ctx.Main} and tag {tag}";
            return;
        }

        var merge = await ctx.Git.MergeNoFf(ctx.Repo, ctx.Staging, msg, ct);
        if (!merge.Ok) throw new StageFailedException($"Merge {ctx.Staging} → {ctx.Main} failed:\n{merge.Output}");

        m.MergeSha = await ctx.Git.RevParse(ctx.Repo, "HEAD", ct);
        rec.Data["mergeSha"] = m.MergeSha;
        ctx.Info($"  merge commit {m.MergeSha[..8]}");

        if (ctx.Config.TagRelease)
        {
            if ((await ctx.Git.Run(ctx.Repo, ct, "rev-parse", "-q", "--verify", $"refs/tags/{tag}")).Ok)
                throw new StageFailedException($"Tag {tag} already exists. Bump versionCode/versionName.");
            var t = await ctx.Git.Tag(ctx.Repo, tag, $"Release {tag} (CI run {m.RunId})", ct);
            if (!t.Ok) throw new StageFailedException($"Tagging failed:\n{t.Output}");
            m.Tag = tag;
            rec.Data["tag"] = tag;
        }

        if (ctx.Config.PushMain)
        {
            var push = await Push(ctx, $"{ctx.Main}:{ctx.Main}", ct);
            if (!push.Ok)
            {
                // Local main is now ahead of remote with an unpushed merge. Roll
                // the local branch back so a re-run starts clean.
                await ctx.Git.Run(ctx.Repo, ct, "reset", "--hard", m.MainShaBefore);
                if (m.Tag != null) await ctx.Git.Run(ctx.Repo, ct, "tag", "-d", m.Tag);
                throw new StageFailedException($"Push of {ctx.Main} failed (local merge reverted):\n{push.Output}");
            }
            if (m.Tag != null)
            {
                var pt = await Push(ctx, $"refs/tags/{m.Tag}", ct);
                if (!pt.Ok) { rec.Status = StageStatus.Warning; ctx.Info($"  [warn] tag push failed: {pt.Output}"); }
            }
        }

        rec.Summary = $"{ctx.Main} ← {ctx.Staging} merge {m.MergeSha[..8]}" +
                      (m.Tag != null ? $", tag {m.Tag}" : "") +
                      (ctx.Config.PushMain ? ", pushed" : ", NOT pushed");
    }

    private static string BuildTag(CiContext ctx)
    {
        var m = ctx.Manifest;
        string name = m.VersionName ?? DateTime.UtcNow.ToString("yyyy.MM.dd");
        return m.VersionCode is int vc
            ? $"{ctx.Config.TagPrefix}{name}+{vc}"
            : $"{ctx.Config.TagPrefix}{name}-{m.StagingSha[..8]}";
    }

    internal static Task<GitResult> Push(CiContext ctx, string refspec, CancellationToken ct)
    {
        bool https = true;
        try
        {
            var url = ctx.Git.Run(ctx.Repo, ct, "remote", "get-url", ctx.Remote).Result.Output;
            https = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }
        catch { }

        return https && !string.IsNullOrWhiteSpace(ctx.GitHubToken)
            ? ctx.Git.PushWithToken(ctx.Repo, ctx.Profile.GitHubOwner, ctx.Profile.GitHubRepo, ctx.GitHubToken, refspec, ct)
            : ctx.Git.Push(ctx.Repo, ctx.Remote, refspec, ct);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 11. BACK-MERGE — dev ← main so dev always contains what shipped
// ─────────────────────────────────────────────────────────────────────────────
public sealed class BackMergeStage : ICiStage
{
    public string Name => "Back-merge to dev";
    public bool ShouldRun(CiContext ctx) => ctx.Config.BackMergeToDev && ctx.Config.PromoteToMain;

    public async Task RunAsync(CiContext ctx, StageRecord rec, StageOutput _, CancellationToken ct)
    {
        var m = ctx.Manifest;
        if (ctx.DryRun) { rec.Status = StageStatus.Skipped; rec.Summary = $"[dry-run] would merge {ctx.Main} into {ctx.Dev}"; return; }

        var sync = await ctx.Git.ResetToRemote(ctx.Repo, ctx.Remote, ctx.Dev, ct);
        if (!sync.Ok) throw new StageFailedException($"Could not sync {ctx.Dev}:\n{sync.Output}");

        var merge = await ctx.Git.MergeNoFf(ctx.Repo, ctx.Main,
            $"Back-merge {ctx.Main} ({m.Tag ?? m.MergeSha?[..8]}) into {ctx.Dev}\n\nCI run {m.RunId}", ct);
        if (!merge.Ok)
        {
            // A conflict here means dev diverged in a way a human must resolve.
            // main is already promoted and pushed — that is the important part.
            rec.Status = StageStatus.Warning;
            rec.Summary = $"CONFLICT merging {ctx.Main} into {ctx.Dev}. Main was promoted; resolve the back-merge manually.";
            rec.Data["conflict"] = merge.Output;
            ctx.Info($"  [warn] {rec.Summary}");
            return;
        }

        m.BackMergeSha = await ctx.Git.RevParse(ctx.Repo, "HEAD", ct);
        rec.Data["backMergeSha"] = m.BackMergeSha;

        if (ctx.Config.PushMain)
        {
            var push = await PromoteStage.Push(ctx, $"{ctx.Dev}:{ctx.Dev}", ct);
            if (!push.Ok)
            {
                rec.Status = StageStatus.Warning;
                rec.Summary = $"Back-merge committed locally but push of {ctx.Dev} failed:\n{push.Output}";
                return;
            }
        }
        rec.Summary = $"{ctx.Dev} ← {ctx.Main} merge {m.BackMergeSha[..8]}, pushed.";
    }
}