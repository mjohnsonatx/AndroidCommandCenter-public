using System.Text.Json.Serialization;

namespace AndroidCommandCenter.Models;

public enum CiStageStatus { Pending, Running, Passed, Failed, Skipped, Warned }

public sealed class CiStageResult
{
    public string Name { get; set; } = "";
    public CiStageStatus Status { get; set; } = CiStageStatus.Pending;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Message { get; set; }

    /// <summary>
    /// Output captured around the point of failure: the lines leading up to it
    /// and the lines after. A bare exit code is not diagnosable after the fact,
    /// and the full log is too large for Firestore.
    /// </summary>
    public List<string> ContextBefore { get; set; } = new();
    public List<string> ContextAfter { get; set; } = new();

    /// <summary>Stage-specific structured data (test counts, lint counts, …).</summary>
    public Dictionary<string, string> Metrics { get; set; } = new();

    [JsonIgnore]
    public TimeSpan Duration =>
        StartedAt is { } s && FinishedAt is { } f ? f - s : TimeSpan.Zero;

    [JsonIgnore] public bool Blocking => Status == CiStageStatus.Failed;
}

/// <summary>
/// The complete, serializable record of one CI run. Written to disk at
/// ~/.local/state/AndroidCommandCenter/ci-runs/ and mirrored to Firestore.
/// Also the input to rollback: PreviousMainSha + MergeCommitSha are what make
/// an automated revert possible.
/// </summary>
public sealed class CiRunManifest
{
    public string RunId { get; set; } = "";
    public string ProjectName { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string Repository { get; set; } = "";
    public string Trigger { get; set; } = "manual";      // manual | schedule | cli
    public string HostName { get; set; } = Environment.MachineName;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }

    public string Remote { get; set; } = "origin";
    public string DevBranch { get; set; } = "dev";
    public string StagingBranch { get; set; } = "staging";
    public string MainBranch { get; set; } = "main";
    public string GateVariant { get; set; } = "Release";

    /// <summary>Commit that was tested — the staging HEAD at run start.</summary>
    public string StagingSha { get; set; } = "";
    public string StagingShaShort { get; set; } = "";

    /// <summary>main's HEAD BEFORE promotion. The rollback target.</summary>
    public string PreviousMainSha { get; set; } = "";

    /// <summary>The --no-ff merge commit created on main. Revert this to roll back.</summary>
    public string? MergeCommitSha { get; set; }
    public string? BackMergeCommitSha { get; set; }
    public string? Tag { get; set; }
    public int? VersionCode { get; set; }
    public string? VersionName { get; set; }

    public bool Pushed { get; set; }
    public bool RolledBack { get; set; }
    public string? RolledBackBy { get; set; }
    public DateTimeOffset? RolledBackAt { get; set; }

    public List<CiStageResult> Stages { get; set; } = new();
    public List<string> Commits { get; set; } = new();
    public string LogPath { get; set; } = "";

    public CiStageStatus Outcome { get; set; } = CiStageStatus.Pending;
    public string Summary { get; set; } = "";

    [JsonIgnore] public bool Success => Outcome == CiStageStatus.Passed;
    [JsonIgnore] public TimeSpan Duration =>
        (FinishedAt ?? DateTimeOffset.Now) - StartedAt;
    [JsonIgnore] public bool CanRollback =>
        Pushed && MergeCommitSha != null && !RolledBack;

    public CiStageResult Stage(string name)
    {
        var s = Stages.FirstOrDefault(x => x.Name == name);
        if (s == null) { s = new CiStageResult { Name = name }; Stages.Add(s); }
        return s;
    }
}