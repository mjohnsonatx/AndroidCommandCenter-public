using System.Text.Json.Serialization;

namespace AndroidCommandCenter.Ci;

public enum StageStatus { Pending, Running, Passed, Failed, Skipped, Warning }

public sealed record CiRunProgress(int Current, int Total, StageRecord Stage);

public sealed class StageRecord
{
    public string Name { get; set; } = "";
    public StageStatus Status { get; set; } = StageStatus.Pending;
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public double DurationSeconds => FinishedAt.HasValue ? (FinishedAt.Value - StartedAt).TotalSeconds : 0;
    public int? ExitCode { get; set; }
    /// <summary>One-line human summary ("12 warnings, 0 errors").</summary>
    public string Summary { get; set; } = "";
    /// <summary>Structured facts for alerting (counts, SHAs, paths).</summary>
    public Dictionary<string, object?> Data { get; set; } = new();
    /// <summary>±50 lines around the first error marker, populated only on failure.</summary>
    public FailureContext? Failure { get; set; }
}

public sealed class FailureContext
{
    public string Message { get; set; } = "";
    public int? ErrorLineIndex { get; set; }
    public List<string> Before { get; set; } = new();
    public string? ErrorLine { get; set; }
    public List<string> After { get; set; } = new();
    public string? Exception { get; set; }

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(Message);
        if (Before.Count > 0) { sb.AppendLine("--- before ---"); Before.ForEach(l => sb.AppendLine(l)); }
        if (ErrorLine != null) { sb.AppendLine(">>> " + ErrorLine); }
        if (After.Count > 0) { sb.AppendLine("--- after ---"); After.ForEach(l => sb.AppendLine(l)); }
        if (Exception != null) { sb.AppendLine("--- exception ---"); sb.AppendLine(Exception); }
        return sb.ToString();
    }
}

public sealed class CommitInfo
{
    public string Sha { get; set; } = "";
    public string Author { get; set; } = "";
    public string Subject { get; set; } = "";
}

/// <summary>
/// Complete, self-describing record of one CI run. Written to disk as JSON and
/// pushed to Firestore as-is. Everything rollback needs is in here.
/// </summary>
public sealed class CiRunManifest
{
    public string RunId { get; set; } = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24];
    public string Trigger { get; set; } = "manual";           // manual | schedule | gui
    public string Host { get; set; } = Environment.MachineName;
    public string Project { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string Repository { get; set; } = "";               // owner/name
    public string Remote { get; set; } = "";
    public string DevBranch { get; set; } = "";
    public string StagingBranch { get; set; } = "";
    public string MainBranch { get; set; } = "";
    public string GateVariant { get; set; } = "";

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public double DurationSeconds => FinishedAt.HasValue ? (FinishedAt.Value - StartedAt).TotalSeconds : 0;

    public bool Success { get; set; }
    public string Result { get; set; } = "";                   // "Promoted v1.2.3", "Failed: Lint", …
    public string? FailedStage { get; set; }
    public bool DryRun { get; set; }

    // Source facts
    public string StagingSha { get; set; } = "";
    public string MainShaBefore { get; set; } = "";            // rollback target (reset) / sanity check
    public string? MergeSha { get; set; }                      // what `git revert -m 1` takes
    public string? BackMergeSha { get; set; }
    public string? Tag { get; set; }
    public int? VersionCode { get; set; }
    public string? VersionName { get; set; }
    public List<CommitInfo> Commits { get; set; } = new();

    public List<StageRecord> Stages { get; set; } = new();
    public string LogPath { get; set; } = "";
    public string ManifestPath { get; set; } = "";

    [JsonIgnore] public StageRecord? CurrentStage => Stages.LastOrDefault(s => s.Status == StageStatus.Running);
}

public sealed class StageFailedException(string message, Exception? inner = null) : Exception(message, inner);
