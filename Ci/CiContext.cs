using AndroidCommandCenter.Models;
using AndroidCommandCenter.Services;

namespace AndroidCommandCenter.Ci;

/// <summary>Everything a stage can see. Immutable inputs, one mutable manifest.</summary>
public sealed class CiContext
{
    public required CiPipelineConfig Config { get; init; }
    public required ProjectProfile Profile { get; init; }
    public required string ProjectPath { get; init; }
    public required string GitHubToken { get; init; }
    public required bool DryRun { get; init; }
    public required CiRunManifest Manifest { get; init; }

    public required ProcessRunner Runner { get; init; }
    public required GitService Git { get; init; }
    public required AndroidService Android { get; init; }
    public required TestReportService TestReports { get; init; }
    public required PipelineLogger Logger { get; init; }

    /// <summary>Human log sink — GUI log box and/or console.</summary>
    public required Action<string> Log { get; init; }

    public string Repo => ProjectPath;
    public string Remote => Config.Remote;
    public string Dev => Config.DevBranch;
    public string Staging => Config.StagingBranch;
    public string Main => Config.MainBranch;

    public void Info(string msg) { Log(msg); Logger.Line(msg); }
}

public interface ICiStage
{
    string Name { get; }
    /// <summary>Return false to skip (records Skipped rather than running).</summary>
    bool ShouldRun(CiContext ctx) => true;
    /// <summary>Throw StageFailedException to fail the run; set record.Summary/Data freely.</summary>
    Task RunAsync(CiContext ctx, StageRecord record, StageOutput output, CancellationToken ct);
}