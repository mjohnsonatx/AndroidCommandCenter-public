using System.Diagnostics;
using System.IO;
using AndroidCommandCenter.Models;

namespace AndroidCommandCenter.Services;

public sealed class LocalCiRunner
{
    private readonly GitService _git;
    private readonly ProcessRunner _runner;
    private readonly TestReportService _testReports;
    private readonly CiRunStore _store;

    public event Action<string>? LogMessage;
    /// <summary>Raised after each stage so the UI can update a live checklist.</summary>
    public event Action<CiStageResult>? StageChanged;

    public LocalCiRunner(GitService git, ProcessRunner runner,
        TestReportService testReports, CiRunStore store)
    {
        _git = git; _runner = runner; _testReports = testReports; _store = store;
    }

    public sealed record CiRequest(
        string ProjectPath,
        string ProjectName,
        CiPipelineConfig Config,
        string GitHubOwner,
        string GitHubRepo,
        string GitHubToken,
        string Trigger);

    public async Task<CiRunManifest> RunAsync(CiRequest req, CancellationToken ct)
    {
        var cfg = req.Config;
        string runId = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

        var m = new CiRunManifest
        {
            RunId = runId,
            ProjectName = req.ProjectName,
            ProjectPath = req.ProjectPath,
            Repository = $"{req.GitHubOwner}/{req.GitHubRepo}",
            Trigger = req.Trigger,
            Remote = cfg.Remote,
            DevBranch = cfg.DevBranch,
            StagingBranch = cfg.StagingBranch,
            MainBranch = cfg.MainBranch,
            GateVariant = cfg.GateVariant,
            Outcome = CiStageStatus.Running
        };

        using var log = new CiLogger(runId, req.ProjectName);
        log.LineWritten += l => LogMessage?.Invoke(l);
        m.LogPath = log.FilePath;

        void Pump(string line) => log.Line(line);
        _runner.OutputReceived += Pump;

        CiWorktree? worktree = null;
        try
        {
            // ══ PREFLIGHT ══════════════════════════════════════════════════
            await Stage(m, log, "Preflight", async st =>
            {
                if (!await _git.IsRepo(req.ProjectPath, ct))
                    return Fail(st, "Project is not a git repository.");

                await _git.EnsureIdentity(req.ProjectPath, ct);

                var fetch = await _git.Fetch(
                    req.ProjectPath, cfg.Remote, req.GitHubToken, ct);
                if (!fetch.Ok) return Fail(st, $"Could not fetch {cfg.Remote}: {fetch.Output}");

                foreach (var b in new[] { cfg.DevBranch, cfg.StagingBranch, cfg.MainBranch })
                    if (!await _git.BranchExistsRemote(req.ProjectPath, cfg.Remote, b, ct))
                        return Fail(st, $"Branch '{cfg.Remote}/{b}' does not exist.");

                if (cfg.PushMain && string.IsNullOrWhiteSpace(req.GitHubToken))
                    return Fail(st, "Pushing is enabled but no GitHub token is configured.");

                st.Message = $"Fetched {cfg.Remote}; all three branches present.";
                return CiStageStatus.Passed;
            });
            if (Stopped(m)) return Finish(m, log, "Preflight failed");

            // ══ RESOLVE COMMITS ════════════════════════════════════════════
            await Stage(m, log, "Resolve staging HEAD", async st =>
            {
                m.StagingSha = await _git.RevParse(req.ProjectPath, $"{cfg.Remote}/{cfg.StagingBranch}", ct);
                m.StagingShaShort = m.StagingSha[..Math.Min(8, m.StagingSha.Length)];
                m.PreviousMainSha = await _git.RevParse(req.ProjectPath, $"{cfg.Remote}/{cfg.MainBranch}", ct);

                m.Commits = await _git.LogBetween(req.ProjectPath,
                    $"{cfg.Remote}/{cfg.MainBranch}", $"{cfg.Remote}/{cfg.StagingBranch}", ct);

                st.Metrics["stagingSha"] = m.StagingShaShort;
                st.Metrics["previousMainSha"] = m.PreviousMainSha[..8];
                st.Metrics["commitsAhead"] = m.Commits.Count.ToString();

                log.Line($"staging  = {m.StagingSha}");
                log.Line($"main     = {m.PreviousMainSha}");
                log.Line($"{m.Commits.Count} commit(s) to promote:");
                foreach (var c in m.Commits.Take(40)) log.Line("  " + c);

                if (m.Commits.Count == 0)
                {
                    st.Message = "staging has no commits that main lacks — nothing to promote.";
                    return CiStageStatus.Skipped;
                }
                st.Message = $"{m.Commits.Count} commit(s) ready to promote.";
                return CiStageStatus.Passed;
            });

            if (m.Stage("Resolve staging HEAD").Status == CiStageStatus.Skipped)
                return Finish(m, log, "Nothing to promote", CiStageStatus.Skipped);
            if (Stopped(m)) return Finish(m, log, "Could not resolve branches");

            // ══ WORKTREE ═══════════════════════════════════════════════════
            await Stage(m, log, "Prepare worktree", async st =>
            {
                worktree = await CiWorktree.CreateAsync(
                    _git, req.ProjectPath, req.ProjectName, l => log.Line(l), ct);
                await worktree.CheckoutAsync(m.StagingSha, ct);
                st.Message = $"Isolated worktree at {worktree.Path}";
                return CiStageStatus.Passed;
            });
            if (Stopped(m) || worktree is null) return Finish(m, log, "Worktree setup failed");

            string wt = worktree.Path;
            var android = new AndroidService(_runner) { ProjectPath = wt, SelectedVariant = cfg.GateVariant };
            Platform.EnsureExecutable(Platform.GradleWrapper(wt) ?? "");

            // ══ VERSION CODE GATE ══════════════════════════════════════════
            if (cfg.RequireVersionCodeBump)
            {
                await Stage(m, log, "Version code", async st =>
                {
                    var v = VersionCodeReader.Read(wt);
                    m.VersionCode = v.Code;
                    m.VersionName = v.Name;
                    st.Metrics["versionCode"] = v.Code?.ToString() ?? "?";
                    st.Metrics["versionName"] = v.Name ?? "?";
                    log.Line($"versionCode={v.Code} versionName={v.Name} (from {v.Source})");

                    if (v.Code is null)
                        return Warn(st, "Could not read versionCode — skipping the bump check.");

                    string? lastTag = await _git.LatestTag(req.ProjectPath, $"{cfg.Remote}/{cfg.MainBranch}", ct);
                    int? lastCode = VersionCodeReader.FromTag(lastTag);
                    st.Metrics["lastTag"] = lastTag ?? "(none)";

                    if (lastTag is null || lastCode is null)
                        return Warn(st, $"No previous release tag with a version code " +
                                        $"(last tag: {lastTag ?? "none"}). Nothing to compare.");

                    if (v.Code <= lastCode)
                        return Fail(st,
                            $"versionCode {v.Code} is not greater than the last release ({lastCode} " +
                            $"from tag {lastTag}). Google Play rejects duplicate version codes and " +
                            "they can never be reused — bump versionCode before promoting.");

                    st.Message = $"versionCode {v.Code} > {lastCode} ✓";
                    return CiStageStatus.Passed;
                });
                if (Stopped(m)) return Finish(m, log, "Version code gate failed");
            }
            else m.Stage("Version code").Status = CiStageStatus.Skipped;

            // ══ CHECKSUM / SUPPLY CHAIN ════════════════════════════════════
            if (cfg.RunChecksumValidation)
            {
                await Stage(m, log, "Checksum validation", async st =>
                {
                    var issues = await ChecksumValidator.ValidateAsync(wt, ct);
                    foreach (var i in issues) log.Line($"[{i.Severity}] {i.Message}");

                    int errors = issues.Count(i => i.Severity == "Error");
                    int warns = issues.Count(i => i.Severity == "Warning");
                    st.Metrics["errors"] = errors.ToString();
                    st.Metrics["warnings"] = warns.ToString();

                    if (errors > 0)
                        return Fail(st, $"{errors} supply-chain problem(s): " +
                            string.Join(" | ", issues.Where(i => i.Severity == "Error").Select(i => i.Message)));
                    if (warns > 0) return Warn(st, $"{warns} supply-chain warning(s).");
                    st.Message = "Toolchain integrity OK.";
                    return CiStageStatus.Passed;
                });
                if (Stopped(m)) return Finish(m, log, "Checksum validation failed");
            }
            else m.Stage("Checksum validation").Status = CiStageStatus.Skipped;

            // ══ SECRET SCAN ════════════════════════════════════════════════
            if (cfg.RunSecretScan)
            {
                await Stage(m, log, "Secret scan", async st =>
                {
                    var (blocking, warnings) = await SecretScanner.ScanAsync(wt, _git, ct);
                    foreach (var f in blocking) log.Line($"[BLOCK] {f.File}:{f.Line} {f.Rule}: {f.Excerpt}");
                    foreach (var f in warnings) log.Line($"[warn ] {f.File}:{f.Line} {f.Rule}: {f.Excerpt}");

                    st.Metrics["blocking"] = blocking.Count.ToString();
                    st.Metrics["warnings"] = warnings.Count.ToString();

                    if (blocking.Count > 0)
                        return Fail(st, $"{blocking.Count} credential(s) found in tracked files: " +
                            string.Join(", ", blocking.Take(5).Select(f => $"{f.File}:{f.Line} ({f.Rule})")));
                    if (warnings.Count > 0) return Warn(st, $"{warnings.Count} possible secret(s) — review.");
                    st.Message = "No credentials detected.";
                    return CiStageStatus.Passed;
                });
                if (Stopped(m)) return Finish(m, log, "Secret scan failed");
            }
            else m.Stage("Secret scan").Status = CiStageStatus.Skipped;

            // ══ LINT ═══════════════════════════════════════════════════════
            if (cfg.RunLint)
            {
                await Stage(m, log, "Lint", async st =>
                {
                    int code = await android.RunLint(cfg.GateVariant, ct);
                    var report = LintReportService.Parse(wt, cfg.GateVariant);
                    log.Line(report.Summary);
                    if (report.Issues.Count > 0) log.Line(report.Detail);

                    st.ExitCode = code;
                    st.Metrics["errors"] = report.Errors.ToString();
                    st.Metrics["warnings"] = report.Warnings.ToString();
                    if (report.HtmlPath != null) st.Metrics["htmlReport"] = report.HtmlPath;

                    if (report.XmlPath == null && code != 0)
                        return Fail(st, $"Lint task failed (exit {code}) without producing a report.");
                    if (report.Errors > 0)
                        return Fail(st, $"Lint found {report.Errors} error(s).");
                    if (cfg.LintFailsOnWarning && report.Warnings > 0)
                        return Fail(st, $"Lint found {report.Warnings} warning(s) and warnings are fatal.");
                    if (report.Warnings > 0)
                        return Warn(st, $"Lint clean of errors; {report.Warnings} warning(s).");

                    st.Message = "Lint clean.";
                    return CiStageStatus.Passed;
                });
                if (Stopped(m)) return Finish(m, log, "Lint failed");
            }
            else m.Stage("Lint").Status = CiStageStatus.Skipped;

            // ══ UNIT TESTS ═════════════════════════════════════════════════
            if (cfg.RunUnitTests)
            {
                await Stage(m, log, "Unit tests", async st =>
                {
                    android.SelectedVariant = cfg.TestVariant;
                    var at = DateTime.Now;
                    int code = await android.RunUnitTests(ct, forceRerun: true);
                    var rpt = _testReports.ParseResults(wt, "UnitTests", at);

                    st.ExitCode = code;
                    st.Metrics["total"] = rpt.Total.ToString();
                    st.Metrics["passed"] = rpt.Passed.ToString();
                    st.Metrics["failed"] = rpt.Failed.ToString();
                    st.Metrics["skipped"] = rpt.Skipped.ToString();

                    log.Line($"Unit tests: {rpt.Passed}/{rpt.Total} passed, " +
                             $"{rpt.Failed} failed, {rpt.Skipped} skipped.");
                    foreach (var f in rpt.Failures)
                        log.Line($"  [X] {f.ClassName}.{f.Name} — {f.FailureMessage}");

                    if (rpt.Total == 0)
                        return code == 0
                            ? Warn(st, "No unit tests executed (task UP-TO-DATE or no tests exist).")
                            : Fail(st, $"Unit test task failed (exit {code}) before any test ran.");
                    if (rpt.Failed > 0)
                        return Fail(st, $"{rpt.Failed} of {rpt.Total} unit tests failed: " +
                            string.Join(", ", rpt.Failures.Take(5).Select(f => $"{f.ClassName}.{f.Name}")));
                    if (code != 0)
                        return Fail(st, $"Unit tests passed but Gradle exited {code}.");

                    st.Message = $"All {rpt.Total} unit tests passed.";
                    return CiStageStatus.Passed;
                });
                if (Stopped(m)) return Finish(m, log, "Unit tests failed");
            }
            else m.Stage("Unit tests").Status = CiStageStatus.Skipped;

            // ══ INSTRUMENT TESTS ═══════════════════════════════════════════
            if (cfg.RunInstrumentTests)
            {
                await Stage(m, log, "Instrument tests", async st =>
                {
                    android.SelectedVariant = cfg.TestVariant;
                    // Unattended CI must not commandeer a physical phone and wipe
                    // its data. Require an emulator; bail if none is available.
                    var avd = new AvdManager(_runner, android.AdbPath, android.EmulatorPath);
                    avd.LogMessage += l => log.Line(l);

                    var devices = await avd.GetDevicesSilent(ct);
                    var emulator = devices.FirstOrDefault(d => d.IsEmulator && d.IsOnline);
                    string? serial = emulator?.Serial
                        ?? await avd.EnsureEmulatorReadyForTests(null, ct);

                    if (serial == null)
                        return Fail(st, "No emulator available for instrument tests. " +
                                        "CI will not run them on a physical device (data wipe).");

                    android.TargetDevice = serial;
                    st.Metrics["device"] = serial;

                    var at = DateTime.Now;
                    int code = await android.RunInstrumentTests(ct);
                    var rpt = _testReports.ParseResults(wt, "InstrumentTests", at);

                    st.ExitCode = code;
                    st.Metrics["total"] = rpt.Total.ToString();
                    st.Metrics["failed"] = rpt.Failed.ToString();
                    log.Line($"Instrument tests: {rpt.Passed}/{rpt.Total} passed, {rpt.Failed} failed.");
                    foreach (var f in rpt.Failures)
                        log.Line($"  [X] {f.ClassName}.{f.Name} — {f.FailureMessage}");

                    if (rpt.Total == 0)
                        return code == 0
                            ? Warn(st, "No instrument tests executed.")
                            : Fail(st, $"Instrument test task failed (exit {code}) before any test ran.");
                    if (rpt.Failed > 0) return Fail(st, $"{rpt.Failed} of {rpt.Total} instrument tests failed.");
                    if (code != 0) return Fail(st, $"Instrument tests passed but Gradle exited {code}.");

                    st.Message = $"All {rpt.Total} instrument tests passed on {serial}.";
                    return CiStageStatus.Passed;
                });
                if (Stopped(m)) return Finish(m, log, "Instrument tests failed");
            }
            else m.Stage("Instrument tests").Status = CiStageStatus.Skipped;

            // ══ ASSEMBLE (proves it actually builds) ═══════════════════════
            await Stage(m, log, "Assemble", async st =>
            {
                int code = await android.AssembleVariant(ct);
                st.ExitCode = code;
                return code == 0
                    ? CiStageStatus.Passed
                    : Fail(st, $"assemble{cfg.GateVariant} failed (exit {code}).");
            });
            if (Stopped(m)) return Finish(m, log, "Assemble failed");

            await Stage(m, log, "Stop Gradle daemons", async st =>
            {
                await android.StopDaemons(ct);
                return CiStageStatus.Passed;
            });

            // ══ ALL GATES PASSED — PROMOTE ═════════════════════════════════
            if (!cfg.PromoteToMain)
            {
                m.Stage("Promote to main").Status = CiStageStatus.Skipped;
                return Finish(m, log, "All gates passed (promotion disabled)", CiStageStatus.Passed);
            }

            // The merge happens in the worktree, on a real local branch, so the
            // primary checkout is still never touched.
            await Stage(m, log, $"Merge {cfg.StagingBranch} -> {cfg.MainBranch}", async st =>
            {
                await _git.Require(wt, ct, "checkout", "-B", cfg.MainBranch,
                    $"{cfg.Remote}/{cfg.MainBranch}");

                string msg =
                    $"ci: promote {cfg.StagingBranch} to {cfg.MainBranch} " +
                    $"({m.StagingShaShort})\n\n" +
                    $"Run: {m.RunId}\n" +
                    $"Commits: {m.Commits.Count}\n" +
                    $"versionCode: {m.VersionCode}\n" +
                    $"versionName: {m.VersionName}\n" +
                    $"Gates: {string.Join(", ", m.Stages
                        .Where(s => s.Status is CiStageStatus.Passed or CiStageStatus.Warned)
                        .Select(s => s.Name))}";

                var r = await _git.MergeNoFf(wt, m.StagingSha, msg, ct);
                if (!r.Ok)
                    return Fail(st,
                        $"Merge into {cfg.MainBranch} conflicted and was aborted. " +
                        "Resolve it manually — CI will not auto-resolve a release merge.\n" + r.Output);

                m.MergeCommitSha = await _git.RevParse(wt, "HEAD", ct);
                st.Metrics["mergeCommit"] = m.MergeCommitSha[..8];
                st.Message = $"Merge commit {m.MergeCommitSha[..8]} created.";
                return CiStageStatus.Passed;
            });
            if (Stopped(m)) return Finish(m, log, "Merge to main failed");

            // ══ TAG ════════════════════════════════════════════════════════
            if (cfg.TagRelease)
            {
                await Stage(m, log, "Tag release", async st =>
                {
                    string version = m.VersionName ?? DateTime.Now.ToString("yyyy.MM.dd");
                    string tag = m.VersionCode is { } vc
                        ? $"{cfg.TagPrefix}{version}+{vc}"
                        : $"{cfg.TagPrefix}{version}";

                    // A duplicate tag means this exact release was already cut.
                    var existing = await _git.Run(wt, ct, "rev-parse", "--verify", $"refs/tags/{tag}");
                    if (existing.Ok)
                        return Fail(st, $"Tag '{tag}' already exists. Bump versionCode/versionName.");

                    var r = await _git.Tag(wt, tag,
                        $"Release {tag}\n\nCI run {m.RunId}\n{m.Commits.Count} commit(s)", ct);
                    if (!r.Ok) return Fail(st, $"Could not create tag: {r.Output}");

                    m.Tag = tag;
                    st.Metrics["tag"] = tag;
                    st.Message = $"Tagged {tag}.";
                    return CiStageStatus.Passed;
                });
                if (Stopped(m)) return Finish(m, log, "Tagging failed");
            }
            else m.Stage("Tag release").Status = CiStageStatus.Skipped;

            // ══ PUSH MAIN ══════════════════════════════════════════════════
            if (cfg.PushMain)
            {
                // Save the manifest BEFORE pushing: if the machine dies during
                // the push, rollback still knows what was attempted.
                _store.Save(m);

                await Stage(m, log, $"Push {cfg.MainBranch}", async st =>
                {
                    var r = await _git.PushWithToken(wt, req.GitHubOwner, req.GitHubRepo,
                        req.GitHubToken, $"{cfg.MainBranch}:{cfg.MainBranch}", ct);
                    if (!r.Ok)
                        return Fail(st,
                            "Push was rejected. main may have moved since the fetch, or the token " +
                            "lacks write access / is blocked by branch protection.\n" + r.Output);

                    m.Pushed = true;

                    if (m.Tag != null)
                    {
                        var tr = await _git.PushWithToken(wt, req.GitHubOwner, req.GitHubRepo,
                            req.GitHubToken, $"refs/tags/{m.Tag}", ct);
                        if (!tr.Ok)
                            return Warn(st, $"main pushed, but tag '{m.Tag}' failed to push: {tr.Output}");
                    }

                    st.Message = $"Pushed {cfg.MainBranch}" + (m.Tag != null ? $" and tag {m.Tag}." : ".");
                    return CiStageStatus.Passed;
                });
                if (Stopped(m)) return Finish(m, log, "Push failed");
            }
            else m.Stage($"Push {cfg.MainBranch}").Status = CiStageStatus.Skipped;

            // ══ BACK-MERGE MAIN -> DEV ═════════════════════════════════════
            // Must happen now: if it is skipped, hotfixes applied to main are
            // silently dropped the next time staging is cut from dev.
            if (cfg.BackMergeToDev)
            {
                await Stage(m, log, $"Back-merge {cfg.MainBranch} -> {cfg.DevBranch}", async st =>
                {
                    await _git.Require(wt, ct, "checkout", "-B", cfg.DevBranch,
                        $"{cfg.Remote}/{cfg.DevBranch}");

                    var r = await _git.MergeNoFf(wt, cfg.MainBranch,
                        $"ci: back-merge {cfg.MainBranch} into {cfg.DevBranch} after {m.Tag ?? m.RunId}", ct);

                    if (!r.Ok)
                        return Warn(st,
                            $"Back-merge conflicted and was aborted. {cfg.MainBranch} is released, but " +
                            $"{cfg.DevBranch} is now behind — resolve manually and merge, or the next " +
                            "release will lose these changes.\n" + r.Output);

                    int ahead = await _git.CountBetween(wt, $"{cfg.Remote}/{cfg.DevBranch}", "HEAD", ct);
                    if (ahead == 0)
                    {
                        st.Message = $"{cfg.DevBranch} was already up to date.";
                        return CiStageStatus.Passed;
                    }

                    m.BackMergeCommitSha = await _git.RevParse(wt, "HEAD", ct);

                    if (cfg.PushMain)
                    {
                        var pr = await _git.PushWithToken(wt, req.GitHubOwner, req.GitHubRepo,
                            req.GitHubToken, $"{cfg.DevBranch}:{cfg.DevBranch}", ct);
                        if (!pr.Ok)
                            return Warn(st, $"Back-merge committed locally but push failed: {pr.Output}");
                    }

                    st.Metrics["backMergeCommit"] = m.BackMergeCommitSha[..8];
                    st.Message = $"{cfg.DevBranch} updated from {cfg.MainBranch}.";
                    return CiStageStatus.Passed;
                });
            }
            else m.Stage($"Back-merge {cfg.MainBranch} -> {cfg.DevBranch}").Status = CiStageStatus.Skipped;

            bool anyWarn = m.Stages.Any(s => s.Status == CiStageStatus.Warned);
            return Finish(m, log,
                anyWarn
                    ? $"Promoted {m.StagingShaShort} to {cfg.MainBranch} with warnings"
                    : $"Promoted {m.StagingShaShort} to {cfg.MainBranch}" +
                      (m.Tag != null ? $" as {m.Tag}" : ""),
                CiStageStatus.Passed);
        }
        catch (OperationCanceledException)
        {
            log.Line("\n[CI] cancelled.");
            return Finish(m, log, "Cancelled", CiStageStatus.Failed);
        }
        catch (Exception ex)
        {
            log.Line($"\n[CI] unhandled error: {ex}");
            return Finish(m, log, $"Unhandled error: {ex.Message}", CiStageStatus.Failed);
        }
        finally
        {
            _runner.OutputReceived -= Pump;
            if (worktree != null) await worktree.DisposeAsync();
        }
    }

    // ── Stage plumbing ─────────────────────────────────────────────────────

    private async Task Stage(CiRunManifest m, CiLogger log, string name,
        Func<CiStageResult, Task<CiStageStatus>> body)
    {
        var st = m.Stage(name);
        if (Stopped(m)) { st.Status = CiStageStatus.Skipped; return; }

        log.Section(name);
        st.Status = CiStageStatus.Running;
        st.StartedAt = DateTimeOffset.Now;
        StageChanged?.Invoke(st);

        var sw = Stopwatch.StartNew();
        try
        {
            st.Status = await body(st);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            st.Status = CiStageStatus.Failed;
            st.Message = ex.Message;
            log.Line($"[EXCEPTION] {ex}");
        }
        finally
        {
            sw.Stop();
            st.FinishedAt = DateTimeOffset.Now;

            if (st.Status is CiStageStatus.Failed or CiStageStatus.Warned)
            {
                // Context BEFORE is already buffered; collect what comes after.
                st.ContextBefore = log.ContextBefore();
                var after = log.BeginCaptureAfter(30);
                log.Line($">>> {name}: {st.Status.ToString().ToUpperInvariant()} " +
                         $"({sw.Elapsed:mm\\:ss}) — {st.Message}");
                await Task.Delay(250);           // let trailing output arrive
                log.EndCaptureAfter();
                st.ContextAfter = after;
            }
            else
            {
                log.Line($">>> {name}: {st.Status.ToString().ToUpperInvariant()} ({sw.Elapsed:mm\\:ss})" +
                         (st.Message is { Length: > 0 } ? $" — {st.Message}" : ""));
            }
            StageChanged?.Invoke(st);
        }
    }

    private static bool Stopped(CiRunManifest m) => m.Stages.Any(s => s.Blocking);

    private static CiStageStatus Fail(CiStageResult st, string message)
    { st.Message = message; return CiStageStatus.Failed; }

    private static CiStageStatus Warn(CiStageResult st, string message)
    { st.Message = message; return CiStageStatus.Warned; }

    private CiRunManifest Finish(CiRunManifest m, CiLogger log, string summary,
        CiStageStatus outcome = CiStageStatus.Failed)
    {
        m.FinishedAt = DateTimeOffset.Now;
        m.Outcome = outcome;
        m.Summary = summary;

        log.Line("");
        log.Header("RESULT",
            $"Outcome:  {outcome}",
            $"Summary:  {summary}",
            $"Duration: {m.Duration:mm\\:ss}");
        foreach (var s in m.Stages)
            log.Line($"  {s.Status,-8} {s.Name}" +
                     (s.Message is { Length: > 0 } ? $" — {s.Message}" : ""));
        if (m.CanRollback)
            log.Line($"\nRollback with: acc-ci rollback --run {m.RunId}");

        _store.Save(m);
        return m;
    }
}
