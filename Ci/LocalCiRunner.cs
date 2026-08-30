using System.IO;
using System.Text.Json;
using AndroidCommandCenter.Models;
using AndroidCommandCenter.Services;

namespace AndroidCommandCenter.Ci;

public sealed class LocalCiRunner
{
    private readonly ProcessRunner _runner;
    private readonly GitService _git;
    private readonly AndroidService _android;
    private readonly TestReportService _tests;
    private readonly FirestoreSink? _firestore;

    public event Action<string>? LogMessage;
    public event Action<StageRecord>? StageChanged;

    public LocalCiRunner(ProcessRunner runner, GitService git, AndroidService android,
                         TestReportService tests, FirestoreSink? firestore = null)
    {
        _runner = runner; _git = git; _android = android; _tests = tests; _firestore = firestore;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static IReadOnlyList<ICiStage> DefaultStages =>
    [
        new PreflightStage(), new SyncStage(), new VersionCodeGate(), new SecretScanStage(),
        new ChecksumStage(), new LintStage(), new UnitTestStage(), new InstrumentTestStage(),
        new BuildStage(), new PromoteStage(), new BackMergeStage()
    ];

    public async Task<CiRunManifest> RunAsync(
        CiPipelineConfig config, ProjectProfile profile, string gitHubToken,
        string trigger, bool dryRun, CancellationToken ct,
        IReadOnlyList<ICiStage>? stages = null)
    {
        stages ??= DefaultStages;
        string project = Path.GetFileName(profile.ProjectPath.TrimEnd('/', '\\'));
        var logger = new PipelineLogger("CI", project, Paths.PipelineLogs);

        var manifest = new CiRunManifest
        {
            Trigger = trigger, Project = project, ProjectPath = profile.ProjectPath,
            Repository = $"{profile.GitHubOwner}/{profile.GitHubRepo}",
            Remote = config.Remote, DevBranch = config.DevBranch,
            StagingBranch = config.StagingBranch, MainBranch = config.MainBranch,
            GateVariant = config.GateVariant, DryRun = dryRun, LogPath = logger.FilePath
        };
        manifest.ManifestPath = Path.Combine(Paths.CiRuns, $"{manifest.RunId}.json");

        _android.ProjectPath = profile.ProjectPath;
        _android.SelectedVariant = config.GateVariant;

        var ctx = new CiContext
        {
            Config = config, Profile = profile, ProjectPath = profile.ProjectPath,
            GitHubToken = gitHubToken, DryRun = dryRun, Manifest = manifest,
            Runner = _runner, Git = _git, Android = _android, TestReports = _tests,
            Logger = logger, Log = m => LogMessage?.Invoke(m)
        };

        ctx.Info($"CI run {manifest.RunId} — {project} — {config.StagingBranch} → {config.MainBranch}" +
                 (dryRun ? " [DRY RUN]" : ""));

        // Remember where we were so the working tree is restored afterwards.
        string? originalBranch = null;
        try { originalBranch = await _git.CurrentBranch(profile.ProjectPath, ct); } catch { }

        try
        {
            foreach (var stage in stages)
            {
                ct.ThrowIfCancellationRequested();
                var rec = new StageRecord { Name = stage.Name, StartedAt = DateTime.UtcNow };
                manifest.Stages.Add(rec);

                if (!stage.ShouldRun(ctx))
                {
                    rec.Status = StageStatus.Skipped; rec.FinishedAt = DateTime.UtcNow;
                    rec.Summary = "Disabled in config.";
                    ctx.Info($"── {stage.Name}: skipped");
                    StageChanged?.Invoke(rec);
                    continue;
                }

                rec.Status = StageStatus.Running;
                StageChanged?.Invoke(rec);
                logger.Section(stage.Name.ToUpperInvariant());
                ctx.Info($"── {stage.Name}");

                var output = new StageOutput();
                void capture(string l) { output.Add(l); logger.Line(l); }
                _runner.OutputReceived += capture;
                _git.LogMessage += capture;
                try
                {
                    await stage.RunAsync(ctx, rec, output, ct);
                    if (rec.Status == StageStatus.Running) rec.Status = StageStatus.Passed;
                    rec.FinishedAt = DateTime.UtcNow;
                    ctx.Info($"   {(rec.Status == StageStatus.Warning ? "⚠" : "✔")} {rec.Summary}  ({rec.DurationSeconds:F0}s)");

                    if (rec.Data.TryGetValue("nothingToPromote", out var n) && n is true)
                    {
                        manifest.Success = true;
                        manifest.Result = "Nothing to promote";
                        ctx.Info("Nothing to promote — run complete.");
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    rec.Status = StageStatus.Failed; rec.FinishedAt = DateTime.UtcNow;
                    rec.Summary = "Cancelled"; rec.Failure = output.Extract("Cancelled by user.");
                    manifest.FailedStage = stage.Name; manifest.Result = "Cancelled";
                    throw;
                }
                catch (Exception ex)
                {
                    rec.Status = StageStatus.Failed; rec.FinishedAt = DateTime.UtcNow;
                    rec.Summary = FirstLine(ex.Message);
                    rec.Failure = output.Extract(ex.Message, ex is StageFailedException ? null : ex);
                    manifest.FailedStage = stage.Name;
                    manifest.Result = $"Failed: {stage.Name} — {rec.Summary}";
                    ctx.Info($"   ✖ {ex.Message}");
                    logger.Line(rec.Failure.ToString());
                    break;
                }
                finally
                {
                    _runner.OutputReceived -= capture;
                    _git.LogMessage -= capture;
                    StageChanged?.Invoke(rec);
                }
            }

            if (manifest.FailedStage == null && string.IsNullOrEmpty(manifest.Result))
            {
                manifest.Success = true;
                manifest.Result = dryRun ? "Dry run passed all gates"
                    : manifest.Tag != null ? $"Promoted {manifest.Tag}"
                    : manifest.MergeSha != null ? $"Promoted {manifest.MergeSha[..8]}"
                    : "All gates passed";
            }
        }
        finally
        {
            manifest.FinishedAt = DateTime.UtcNow;

            // Stop gradle daemons — a scheduled run must not leave 3 GB of JVMs behind.
            try { using var c = new CancellationTokenSource(20_000); await _android.StopDaemons(c.Token); } catch { }

            // Restore the user's branch so a GUI run doesn't leave them on `main`.
            if (originalBranch is { Length: > 0 } && originalBranch != "HEAD")
                try { await _git.Run(profile.ProjectPath, CancellationToken.None, "checkout", "-q", originalBranch); } catch { }

            ctx.Info($"Result: {manifest.Result} ({manifest.DurationSeconds:F0}s)");
            logger.Save();
            SaveManifest(manifest);

            if (_firestore != null && config.PublishToFirestore)
            {
                try
                {
                    await _firestore.PublishAsync(manifest, CancellationToken.None);
                    ctx.Info($"Published to Firestore: {config.FirestoreCollection}/{manifest.RunId}");
                }
                catch (Exception ex) { ctx.Info($"[warn] Firestore publish failed: {ex.Message}"); }
            }
        }
        return manifest;
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();

    public static void SaveManifest(CiRunManifest m)
    {
        try
        {
            var tmp = m.ManifestPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(m, Json));
            File.Move(tmp, m.ManifestPath, overwrite: true);
        }
        catch { }
    }

    public static IEnumerable<CiRunManifest> LoadManifests() =>
        Directory.Exists(Paths.CiRuns)
            ? Directory.GetFiles(Paths.CiRuns, "*.json")
                .OrderByDescending(f => f)
                .Select(f => { try { return JsonSerializer.Deserialize<CiRunManifest>(File.ReadAllText(f), Json); } catch { return null; } })
                .Where(m => m != null)!
            : [];
}