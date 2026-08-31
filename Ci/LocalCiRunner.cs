using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    public event Action<CiRunProgress>? ProgressChanged;

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
        int progressTotal = stages.Count + 1; // configured stages + daemon cleanup
        string project = Path.GetFileName(profile.ProjectPath.TrimEnd('/', '\\'));
        var logger = new PipelineLogger("CI", project, Paths.PipelineLogs);

        var manifest = new CiRunManifest
        {
            Trigger = trigger, Project = project, ProjectPath = profile.ProjectPath,
            Repository = $"{profile.GitHubOwner}/{profile.GitHubRepo}",
            Remote = config.Remote, DevBranch = config.DevBranch,
            StagingBranch = config.StagingBranch, MainBranch = config.MainBranch,
            GateVariant = config.GateVariant, TestVariant = config.TestVariant,
            DryRun = dryRun, LogPath = logger.FilePath
        };
        manifest.ManifestPath = Path.Combine(Paths.CiRuns, $"{manifest.RunId}.json");

        CiWorktree worktree;
        FileStream? runLock = null;
        try
        {
            runLock = AcquireRunLock(profile.ProjectPath);
            worktree = await CiWorktree.CreateAsync(
                _git, profile.ProjectPath, project,
                m => LogMessage?.Invoke(m), ct, keep: true);
        }
        catch (OperationCanceledException)
        {
            runLock?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            runLock?.Dispose();
            var failed = new StageRecord
            {
                Name = "Prepare isolated worktree",
                Status = StageStatus.Failed,
                StartedAt = DateTime.UtcNow,
                FinishedAt = DateTime.UtcNow,
                Summary = ex.Message,
                Failure = new FailureContext { Message = ex.Message, Exception = ex.ToString() }
            };
            manifest.Stages.Add(failed);
            manifest.FailedStage = failed.Name;
            manifest.Result = $"Failed: {failed.Name} — {ex.Message}";
            manifest.FinishedAt = DateTime.UtcNow;
            logger.Line(manifest.Result);
            logger.Save();
            SaveManifest(manifest);
            return manifest;
        }

        _android.ProjectPath = worktree.Path;
        _android.SelectedVariant = config.GateVariant;

        var ctx = new CiContext
        {
            Config = config, Profile = profile, ProjectPath = worktree.Path,
            GitHubToken = gitHubToken, DryRun = dryRun, Manifest = manifest,
            Runner = _runner, Git = _git, Android = _android, TestReports = _tests,
            Logger = logger, Log = m => LogMessage?.Invoke(m)
        };

        ctx.Info($"CI run {manifest.RunId} — {project} — {config.StagingBranch} → {config.MainBranch}" +
                 (dryRun ? " [DRY RUN]" : ""));

        try
        {
            for (int stageIndex = 0; stageIndex < stages.Count; stageIndex++)
            {
                ct.ThrowIfCancellationRequested();
                var stage = stages[stageIndex];
                var rec = new StageRecord { Name = stage.Name, StartedAt = DateTime.UtcNow };
                manifest.Stages.Add(rec);

                void ReportProgress()
                {
                    StageChanged?.Invoke(rec);
                    ProgressChanged?.Invoke(new CiRunProgress(stageIndex + 1, progressTotal, rec));
                }

                if (!stage.ShouldRun(ctx))
                {
                    rec.Status = StageStatus.Skipped; rec.FinishedAt = DateTime.UtcNow;
                    rec.Summary = "Disabled in config.";
                    ctx.Info($"── {stage.Name}: skipped");
                    ReportProgress();
                    continue;
                }

                rec.Status = StageStatus.Running;
                ReportProgress();
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
                    ReportProgress();
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

            // Stop Gradle daemons, but keep this final command attached to the
            // operation token so the UI Abort button can terminate it too.
            var cleanup = new StageRecord
            {
                Name = "Stop Gradle daemons",
                Status = ct.IsCancellationRequested ? StageStatus.Skipped : StageStatus.Running,
                StartedAt = DateTime.UtcNow,
                Summary = ct.IsCancellationRequested ? "Skipped because the run was aborted." : ""
            };
            manifest.Stages.Add(cleanup);
            ProgressChanged?.Invoke(new CiRunProgress(progressTotal, progressTotal, cleanup));
            if (!ct.IsCancellationRequested)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    await _android.StopDaemons(timeout.Token);
                    cleanup.Status = StageStatus.Passed;
                    cleanup.Summary = "Gradle daemons stopped.";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cleanup.Status = StageStatus.Skipped;
                    cleanup.Summary = "Daemon cleanup aborted by user.";
                }
                catch (Exception ex)
                {
                    cleanup.Status = StageStatus.Warning;
                    cleanup.Summary = ex.Message;
                }
            }
            cleanup.FinishedAt = DateTime.UtcNow;
            ProgressChanged?.Invoke(new CiRunProgress(progressTotal, progressTotal, cleanup));

            ctx.Info($"Result: {manifest.Result} ({manifest.DurationSeconds:F0}s)");

            if (_firestore != null && config.PublishToFirestore)
            {
                try
                {
                    await _firestore.PublishAsync(manifest, CancellationToken.None);
                    ctx.Info($"Published to Firestore: {config.FirestoreCollection}/{manifest.RunId}");
                }
                catch (Exception ex) { ctx.Info($"[warn] Firestore publish failed: {ex.Message}"); }
            }

            logger.Save();
            SaveManifest(manifest);
            try { await worktree.DisposeAsync(); }
            finally { runLock?.Dispose(); }
        }
        return manifest;
    }

    private static FileStream AcquireRunLock(string projectPath)
    {
        string locks = Path.Combine(Paths.StateDir, "ci-locks");
        Directory.CreateDirectory(locks);
        string canonical = Path.GetFullPath(projectPath);
        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16];
        string path = Path.Combine(locks, $"{id}.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, Encoding.UTF8, 1024, leaveOpen: true);
            writer.WriteLine($"pid={Environment.ProcessId}");
            writer.WriteLine($"project={canonical}");
            writer.WriteLine($"started={DateTime.UtcNow:O}");
            writer.Flush();
            stream.Position = 0;
            return stream;
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "Another CI/CD run is already active for this project. Wait for it to finish or cancel it first.", ex);
        }
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
                .OfType<CiRunManifest>()
            : [];
}
