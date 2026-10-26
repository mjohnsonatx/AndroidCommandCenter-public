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
        var java = ctx.Android.JavaEnvironment;
        if (java.Launcher is null)
            problems.Add(java.SelectionReason);
        foreach (int required in java.RequiredToolchainMajors)
            if (!java.Installations.Any(j => j.Major == required && j.IsJdk))
                problems.Add($"JDK {required} compile toolchain not found locally.");

        foreach (var b in new[] { ctx.Dev, ctx.Staging, ctx.Main })
            if (!Models.GitNames.IsValid(b)) problems.Add($"Invalid branch name '{b}'.");
        if (new[] { ctx.Dev, ctx.Staging, ctx.Main }.Distinct(StringComparer.Ordinal).Count() != 3)
            problems.Add("Development, staging, and production must be different branches.");
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
        var fetch = await ctx.Git.Fetch(ctx.Repo, ctx.Remote, ctx.GitHubToken, ct);
        if (!fetch.Ok) throw new StageFailedException($"git fetch failed:\n{fetch.Output}");

        // Repositories created before GitHub changed its default commonly use
        // master; newer repositories commonly use main. If the saved production
        // name is the wrong convention, select the one that actually exists.
        if (!await ctx.Git.BranchExistsRemote(ctx.Repo, ctx.Remote, ctx.Main, ct))
        {
            string? detected = null;
            foreach (string candidate in new[] { "master", "main" })
            {
                if (string.Equals(candidate, ctx.Main, StringComparison.OrdinalIgnoreCase)) continue;
                if (await ctx.Git.BranchExistsRemote(ctx.Repo, ctx.Remote, candidate, ct))
                {
                    detected = candidate;
                    break;
                }
            }

            if (detected is not null)
            {
                string configured = ctx.Main;
                ctx.Config.MainBranch = detected;
                ctx.Manifest.MainBranch = detected;
                rec.Data["configuredProductionBranch"] = configured;
                rec.Data["detectedProductionBranch"] = detected;
                ctx.Info($"  [config] {ctx.Remote}/{configured} is absent; " +
                         $"using {ctx.Remote}/{detected} as the production branch.");
            }
        }

        foreach (var b in new[] { ctx.Staging, ctx.Main, ctx.Dev })
            if (!await ctx.Git.BranchExistsRemote(ctx.Repo, ctx.Remote, b, ct))
                throw new StageFailedException($"Branch '{b}' does not exist on '{ctx.Remote}'.");
        if (new[] { ctx.Dev, ctx.Staging, ctx.Main }.Distinct(StringComparer.Ordinal).Count() != 3)
            throw new StageFailedException("Development, staging, and production must be different branches.");

        var checkout = await ctx.Git.Run(
            ctx.Repo, ct, "checkout", "--detach", $"{ctx.Remote}/{ctx.Staging}");
        if (!checkout.Ok)
            throw new StageFailedException($"Could not check out {ctx.Remote}/{ctx.Staging}:\n{checkout.Output}");
        var reset = await ctx.Git.Run(
            ctx.Repo, ct, "reset", "--hard", $"{ctx.Remote}/{ctx.Staging}");
        if (!reset.Ok) throw new StageFailedException($"Could not sync {ctx.Staging}:\n{reset.Output}");
        await ctx.Git.Run(ctx.Repo, ct, "clean", "-fd");

        // keystore.properties is intentionally gitignored, so Git cannot put it
        // in the isolated worktree. Pull the local copy from the selected Android
        // checkout after cleaning and before any Gradle task runs.
        CiWorktree.CopyLocalSigningConfiguration(
            ctx.Profile.ProjectPath, ctx.ProjectPath, ctx.Info);

        var m = ctx.Manifest;
        m.StagingSha = await ctx.Git.RevParse(ctx.Repo, "HEAD", ct);
        m.MainShaBefore = await ctx.Git.RevParse(ctx.Repo, $"{ctx.Remote}/{ctx.Main}", ct);

        // Check history before the no-op shortcut, including merge-only history.
        // A merge preserves main's commits, but its result might differ from the
        // staging tree that passed the gates. Both metadata and hotfix gaps block.
        var behind = await ctx.Git.CountBetween(ctx.Repo, m.StagingSha, m.MainShaBefore, ct);
        var devSha = await ctx.Git.RevParse(ctx.Repo, $"{ctx.Remote}/{ctx.Dev}", ct);
        var devBehind = await ctx.Git.CountBetween(ctx.Repo, devSha, m.MainShaBefore, ct);
        rec.Data["stagingMissingProductionCommits"] = behind;
        rec.Data["devMissingProductionCommits"] = devBehind;
        if (behind > 0)
        {
            string missing = await ctx.Git.Require(ctx.Repo, ct, "log", "--format=%h %s",
                $"{m.StagingSha}..{m.MainShaBefore}");
            string[] bases = (await ctx.Git.Require(ctx.Repo, ct, "merge-base", "--all",
                m.StagingSha, m.MainShaBefore)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string mainTree = await ctx.Git.RevParse(ctx.Repo, $"{m.MainShaBefore}^{{tree}}", ct);
            bool historyOnly = bases.Length == 1 &&
                mainTree == await ctx.Git.RevParse(ctx.Repo, $"{bases[0]}^{{tree}}", ct);
            rec.Data["missingProductionCommits"] = missing;
            rec.Data["productionHistoryOnly"] = historyOnly;
            string delta = historyOnly
                ? "History-only divergence: production has no net file changes relative to the common ancestor."
                : bases.Length == 1
                    ? "File-content divergence: production has file changes relative to the common ancestor."
                    : "Multiple common ancestors: file-content divergence cannot be classified safely.";
            throw new StageFailedException(
                $"{ctx.Staging} does not contain {behind} commit(s) from current {ctx.Main} history. " +
                "The production merge could produce a file tree different from the staging commit built and tested.\n" +
                $"{delta}\nMissing commits:\n{missing}\n" +
                $"Merge {ctx.Main} into {ctx.Staging}, rebuild, and rerun promotion. " +
                (devBehind == 0
                    ? $"{ctx.Dev} is already synchronized with {ctx.Main}."
                    : $"Also merge {ctx.Main} into {ctx.Dev}, which is missing {devBehind} production commit(s)."));
        }

        // What are we about to promote?
        var raw = await ctx.Git.Require(ctx.Repo, ct, "log",
            "--pretty=format:%H%x1f%an%x1f%s", $"{m.MainShaBefore}..{m.StagingSha}");
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

        string configuredVariant = ctx.Config.GateVariant;
        string configuredTestVariant = string.IsNullOrWhiteSpace(ctx.Config.TestVariant)
            ? "Debug" : ctx.Config.TestVariant;
        VariantResolution variant;
        VariantResolution testVariant;
        try
        {
            var detector = new FlavorDetector();
            variant = detector.ResolveGateVariant(
                ctx.ProjectPath, configuredVariant, ctx.Staging);
            testVariant = detector.ResolveGateVariant(
                ctx.ProjectPath, configuredTestVariant, ctx.Staging);
        }
        catch (Exception ex)
        {
            throw new StageFailedException(ex.Message, ex);
        }
        ctx.Config.GateVariant = variant.Resolved;
        ctx.Config.TestVariant = testVariant.Resolved;
        ctx.Manifest.GateVariant = variant.Resolved;
        ctx.Manifest.TestVariant = testVariant.Resolved;
        ctx.Android.SelectedVariant = variant.Resolved;
        rec.Data["configuredGateVariant"] = configuredVariant;
        rec.Data["resolvedGateVariant"] = variant.Resolved;
        rec.Data["configuredTestVariant"] = configuredTestVariant;
        rec.Data["resolvedTestVariant"] = testVariant.Resolved;
        rec.Data["availableVariants"] = variant.Available;
        if (!configuredVariant.Equals(variant.Resolved, StringComparison.Ordinal))
            ctx.Info($"  [build variant] {configuredVariant} → {variant.Resolved} — {variant.Reason}");
        if (!configuredTestVariant.Equals(testVariant.Resolved, StringComparison.Ordinal))
            ctx.Info($"  [test variant] {configuredTestVariant} → {testVariant.Resolved} — {testVariant.Reason}");

        rec.Summary = $"{m.Commits.Count} commit(s) to promote; " +
                      $"build={variant.Resolved}, tests={testVariant.Resolved}. " +
                      $"staging={m.StagingSha[..8]} main={m.MainShaBefore[..8]}";
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
        int code = await ctx.Android.RunLint(ct);
        var report = LintReportService.Parse(
            ctx.ProjectPath, AndroidService.VerificationVariant);

        rec.ExitCode = code;
        rec.Data["errors"] = report.Errors;
        rec.Data["warnings"] = report.Warnings;
        rec.Data["xml"] = report.XmlPath;
        rec.Data["html"] = report.HtmlPath;
        rec.Data["topIssues"] = report.Issues
            .Where(i => i.Severity is "Fatal" or "Error")
            .Take(25).Select(i => $"{i.Id}: {i.Message} @ {i.Location}").ToList();

        if (report.XmlPath is null)
            throw new StageFailedException($"Lint exited {code} and produced no report. Is the lint task configured for '{AndroidService.VerificationVariant}'?");

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
        var at = DateTime.Now;
        int code = await ctx.Android.RunUnitTests(ct, forceTestRerun: true);
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
        var avd = new AvdManager(ctx.Runner, ctx.Android.AdbPath, ctx.Android.EmulatorPath);
        avd.LogMessage += ctx.Info;
        var devices = await avd.GetDevicesSilent(ct);
        string? serial = devices.FirstOrDefault(d => d.IsEmulator && d.IsOnline)?.Serial
                         ?? await avd.EnsureEmulatorReadyForTests(null, ct);
        if (serial is null)
            throw new StageFailedException(
                "No online emulator is available. CI will not run instrument tests on a physical phone.");
        ctx.Android.TargetDevice = serial;
        rec.Data["emulator"] = serial;

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
// 10. PROMOTE — main + staging ← exact release merge, atomic push with tag
// ─────────────────────────────────────────────────────────────────────────────
public sealed class PromoteStage : ICiStage
{
    public string Name => "Promote to production";
    public bool ShouldRun(CiContext ctx) => ctx.Config.PromoteToMain;

    public async Task RunAsync(CiContext ctx, StageRecord rec, StageOutput _, CancellationToken ct)
    {
        var m = ctx.Manifest;
        await ctx.Git.EnsureIdentity(ctx.Repo, ct);

        await ValidateHeads(ctx, ct);
        var checkout = await ctx.Git.Run(
            ctx.Repo, ct, "checkout", "--detach", m.MainShaBefore);
        if (!checkout.Ok) throw new StageFailedException($"Could not check out {ctx.Main}:\n{checkout.Output}");
        var sync = await ctx.Git.Run(ctx.Repo, ct, "reset", "--hard", m.MainShaBefore);
        if (!sync.Ok) throw new StageFailedException($"Could not sync {ctx.Main}:\n{sync.Output}");

        string tag = BuildTag(ctx);
        if (ctx.Config.TagRelease)
        {
            Models.GitNames.Require(tag, "tag");
            if ((await ctx.Git.Run(ctx.Repo, ct, "show-ref", "--verify", "--quiet", $"refs/tags/{tag}")).Ok)
                throw new StageFailedException($"Tag {tag} already exists. Bump versionCode/versionName.");
        }
        string msg = $"Release {tag}: merge {ctx.Staging} into {ctx.Main}\n\n" +
                     $"CI run {m.RunId}\n{m.Commits.Count} commit(s):\n" +
                     string.Join("\n", m.Commits.Take(50).Select(c => $"- {c.Sha[..8]} {c.Subject}"));

        if (ctx.DryRun)
        {
            rec.Status = StageStatus.Skipped;
            rec.Summary = $"[dry-run] would merge tested {ctx.Staging}@{m.StagingSha[..8]} into {ctx.Main}" +
                (ctx.Config.PushMain ? $" and atomically advance {ctx.Main} and {ctx.Staging}" : " locally (no push)") +
                (ctx.Config.TagRelease ? $" with tag {tag}" : "");
            return;
        }

        var merge = await ctx.Git.MergeNoFf(ctx.Repo, m.StagingSha, msg, ct);
        if (!merge.Ok) throw new StageFailedException($"Merge {ctx.Staging} → {ctx.Main} failed:\n{merge.Output}");

        m.MergeSha = await ctx.Git.RevParse(ctx.Repo, "HEAD", ct);
        rec.Data["mergeSha"] = m.MergeSha;
        string testedTree = await ctx.Git.RevParse(ctx.Repo, $"{m.StagingSha}^{{tree}}", ct);
        string releaseTree = await ctx.Git.RevParse(ctx.Repo, $"{m.MergeSha}^{{tree}}", ct);
        rec.Data["testedTree"] = testedTree;
        rec.Data["releaseTree"] = releaseTree;
        if (testedTree != releaseTree)
            throw new StageFailedException("Release tree differs from the tested staging tree. Nothing was pushed; rebuild and retest the combined changes.");
        string parents = await ctx.Git.Require(ctx.Repo, ct, "rev-list", "--parents", "-n", "1", m.MergeSha);
        if (parents != $"{m.MergeSha} {m.MainShaBefore} {m.StagingSha}")
            throw new StageFailedException("Release must be a two-parent merge of the captured production and tested staging heads. Nothing was pushed.");
        ctx.Info($"  merge commit {m.MergeSha[..8]}");

        string? tagObject = null;
        try
        {
            if (ctx.Config.TagRelease)
            {
                var t = await ctx.Git.Tag(ctx.Repo, tag, $"Release {tag} (CI run {m.RunId})", ct);
                if (!t.Ok) throw new StageFailedException($"Tagging failed:\n{t.Output}");
                m.Tag = tag;
                // Capture ownership even if cancellation arrives after tag creation.
                using var lookup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                tagObject = await ctx.Git.RevParse(ctx.Repo, $"refs/tags/{tag}", lookup.Token);
                rec.Data["tag"] = tag;
                ct.ThrowIfCancellationRequested();
            }
            if (ctx.Config.PushMain)
            {
                // Gates and local merge/tag hooks may take time. Re-fetch both
                // heads immediately before the guarded, all-or-nothing push.
                await ValidateHeads(ctx, ct);
                var specs = new List<string>
                {
                    $"{m.MergeSha}:refs/heads/{ctx.Main}",
                    $"{m.MergeSha}:refs/heads/{ctx.Staging}"
                };
                var expected = new Dictionary<string, string>
                {
                    [$"refs/heads/{ctx.Main}"] = m.MainShaBefore,
                    [$"refs/heads/{ctx.Staging}"] = m.StagingSha
                };
                if (m.Tag != null)
                {
                    specs.Add($"{tagObject}:refs/tags/{m.Tag}");
                    expected[$"refs/tags/{m.Tag}"] = ""; // must not exist
                }
                m.ReleasePushStatus = "Unknown"; // cancellation/transport loss is not a confirmed rejection
                var push = await ctx.Git.PushAtomic(ctx.Repo, ctx.Remote, specs, expected, ctx.GitHubToken, ct);
                rec.Data["pushOutput"] = push.Output;
                if (!push.Ok)
                {
                    await InspectPushOutcome(ctx, rec, tagObject);
                    if (m.ReleasePushStatus != "Published")
                        throw new StageFailedException($"Atomic release push failed ({m.ReleasePushStatus}). " +
                            "No non-atomic fallback was attempted. Inspect remote refs before retrying.\n" + push.Output);
                    rec.Status = StageStatus.Warning;
                    ctx.Info("  [warn] Push reported an error, but remote refs confirm the entire release was published.");
                }
                m.ReleasePushStatus = "Published";
                m.MainShaAfter = m.StagingShaAfter = m.MergeSha;
                rec.Data["mainShaAfter"] = m.MainShaAfter;
                rec.Data["stagingShaAfter"] = m.StagingShaAfter;
            }
            else m.ReleasePushStatus = "LocalOnly";
        }
        catch
        {
            if (m.ReleasePushStatus == "Unknown")
            {
                m.SynchronizationWarning = "Release push outcome is uncertain. Inspect production, staging, and the tag before retrying; dev may still need the release back-merge.";
                ctx.Info($"  [warn] {m.SynchronizationWarning}");
            }
            // Tags are shared with the primary checkout. Remove only our exact
            // newly created tag, and only when the release was not published.
            if (tagObject != null && m.ReleasePushStatus is "NotAttempted" or "Rejected")
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var removed = await ctx.Git.Run(ctx.Repo, cleanup.Token, "update-ref", "-d", $"refs/tags/{tag}", tagObject);
                    if (removed.Ok) m.Tag = null;
                    else ctx.Info($"  [warn] Could not remove unpublished local tag {tag}: {removed.Output}");
                }
                catch (Exception ex) { ctx.Info($"  [warn] Could not remove unpublished local tag {tag}: {ex.Message}"); }
            }
            throw;
        }
        finally
        {
            rec.Data["releasePushStatus"] = m.ReleasePushStatus;
        }

        rec.Summary = (ctx.Config.PushMain
            ? $"{ctx.Main} and {ctx.Staging} → release {m.MergeSha[..8]}, pushed atomically"
            : $"Local release {m.MergeSha[..8]}, NOT pushed; remote branches unchanged") +
                      (m.Tag != null ? $", tag {m.Tag}" : "") +
                      "; release tree matches tested staging.";
    }

    private static async Task ValidateHeads(CiContext ctx, CancellationToken ct)
    {
        var fetch = await ctx.Git.Fetch(ctx.Repo, ctx.Remote, ctx.GitHubToken, ct);
        if (!fetch.Ok) throw new StageFailedException($"Could not refresh {ctx.Remote}:\n{fetch.Output}");
        foreach (var (branch, expected) in new[]
                 { (ctx.Main, ctx.Manifest.MainShaBefore), (ctx.Staging, ctx.Manifest.StagingSha) })
        {
            if (!await ctx.Git.BranchExistsRemote(ctx.Repo, ctx.Remote, branch, ct))
                throw new StageFailedException($"{branch} was removed during the run. Nothing was pushed.");
            string actual = await ctx.Git.RevParse(ctx.Repo, $"refs/remotes/{ctx.Remote}/{branch}", ct);
            if (actual != expected)
                throw new StageFailedException($"{branch} changed during the run ({expected} → {actual}). " +
                    "Nothing was pushed. Rerun all gates against the new heads.");
        }
        if (await ctx.Git.CountBetween(ctx.Repo, ctx.Manifest.StagingSha, ctx.Manifest.MainShaBefore, ct) != 0)
            throw new StageFailedException($"{ctx.Staging} must contain current {ctx.Main} before promotion. Synchronize, rebuild, and retest.");
    }

    private static async Task InspectPushOutcome(CiContext ctx, StageRecord rec, string? tagObject)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var refs = await ctx.Git.ReadRemoteRefs(ctx.Repo, ctx.Remote, ctx.GitHubToken, timeout.Token);
            string? main = refs.GetValueOrDefault($"refs/heads/{ctx.Main}");
            string? staging = refs.GetValueOrDefault($"refs/heads/{ctx.Staging}");
            string? tag = ctx.Manifest.Tag is { } name ? refs.GetValueOrDefault($"refs/tags/{name}") : null;
            rec.Data["observedMainSha"] = main;
            rec.Data["observedStagingSha"] = staging;
            rec.Data["observedTagObject"] = tag;
            var m = ctx.Manifest;
            if (main == m.MergeSha && staging == m.MergeSha && tag == tagObject)
                m.ReleasePushStatus = "Published";
            else if (main == m.MainShaBefore && staging == m.StagingSha && (tagObject == null || tag != tagObject))
                m.ReleasePushStatus = "Rejected";
            // Partial or subsequently moved heads remain Unknown. They might be
            // descendants of a published release, especially with tagging off.
        }
        catch (Exception ex) { rec.Data["pushVerificationError"] = ex.Message; }
    }

    private static string BuildTag(CiContext ctx)
    {
        var m = ctx.Manifest;
        string name = m.VersionName ?? DateTime.UtcNow.ToString("yyyy.MM.dd");
        return m.VersionCode is int vc
            ? $"{ctx.Config.TagPrefix}{name}+{vc}"
            : $"{ctx.Config.TagPrefix}{name}-{m.StagingSha[..8]}";
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
        string releaseState = ctx.Manifest.ReleasePushStatus == "Published"
            ? $"Production ({ctx.Main}) and {ctx.Staging} were published successfully."
            : "The release exists only locally; remote release branches were not changed.";
        try { await MergeAsync(ctx, rec, ct); }
        catch (OperationCanceledException)
        {
            ctx.Manifest.SynchronizationWarning = $"{releaseState} {ctx.Dev} back-merge was cancelled; inspect dev before retrying.";
            ctx.Info($"  [warn] {ctx.Manifest.SynchronizationWarning}");
            throw;
        }
        catch (Exception ex)
        {
            rec.Status = StageStatus.Warning;
            rec.Summary = $"{ctx.Dev} back-merge failed: {ex.Message}";
        }
        if (rec.Status == StageStatus.Warning)
        {
            rec.Summary = $"{releaseState} {rec.Summary} " +
                $"Resolve/push the exact release {ctx.Manifest.MergeSha} into {ctx.Dev}; do not rerun the release push.";
            ctx.Manifest.SynchronizationWarning = rec.Summary;
            ctx.Info($"  [warn] {rec.Summary}");
        }
    }

    private static async Task MergeAsync(CiContext ctx, StageRecord rec, CancellationToken ct)
    {
        var m = ctx.Manifest;
        if (ctx.DryRun) { rec.Status = StageStatus.Skipped; rec.Summary = $"[dry-run] would merge {ctx.Main} into {ctx.Dev}"; return; }

        var fetch = await ctx.Git.Fetch(ctx.Repo, ctx.Remote, ctx.GitHubToken, ct);
        if (!fetch.Ok) throw new StageFailedException($"Could not refresh {ctx.Remote}:\n{fetch.Output}");
        var checkout = await ctx.Git.Run(
            ctx.Repo, ct, "checkout", "--detach", $"{ctx.Remote}/{ctx.Dev}");
        if (!checkout.Ok) throw new StageFailedException($"Could not check out {ctx.Dev}:\n{checkout.Output}");
        var sync = await ctx.Git.Run(ctx.Repo, ct, "reset", "--hard", $"{ctx.Remote}/{ctx.Dev}");
        if (!sync.Ok) throw new StageFailedException($"Could not sync {ctx.Dev}:\n{sync.Output}");

        var merge = await ctx.Git.MergeNoFf(ctx.Repo, m.MergeSha!,
            $"Back-merge {ctx.Main} ({m.Tag ?? m.MergeSha?[..8]}) into {ctx.Dev}\n\nCI run {m.RunId}", ct);
        if (!merge.Ok)
        {
            rec.Status = StageStatus.Warning;
            rec.Summary = $"CONFLICT merging the release into {ctx.Dev}.";
            rec.Data["conflict"] = merge.Output;
            ctx.Info($"  [warn] {rec.Summary}");
            return;
        }

        m.BackMergeSha = await ctx.Git.RevParse(ctx.Repo, "HEAD", ct);
        rec.Data["backMergeSha"] = m.BackMergeSha;

        if (ctx.Config.PushMain)
        {
            var push = await ctx.Git.PushToRemote(ctx.Repo, ctx.Remote,
                $"{m.BackMergeSha}:refs/heads/{ctx.Dev}", ctx.GitHubToken, ct);
            if (!push.Ok)
            {
                rec.Status = StageStatus.Warning;
                rec.Summary = $"Back-merge committed locally but push of {ctx.Dev} failed:\n{push.Output}";
                return;
            }
            m.DevShaAfter = m.BackMergeSha;
            rec.Data["devShaAfter"] = m.DevShaAfter;
        }
        rec.Summary = $"{ctx.Dev} ← {ctx.Main} merge {m.BackMergeSha[..8]}" +
                      (ctx.Config.PushMain ? ", pushed." : ", NOT pushed.");
    }
}
