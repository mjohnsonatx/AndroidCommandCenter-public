namespace AndroidCommandCenter.Models;

/// <summary>
/// The local CI/CD promotion pipeline: dev → staging → production, then
/// back-merge production → dev. Merges are --no-ff so every release is one
/// revertable commit.
/// </summary>
public sealed class CiPipelineConfig
{
    public string Remote { get; set; } = "origin";
    public string DevBranch { get; set; } = "dev";
    public string StagingBranch { get; set; } = "staging";
    public string MainBranch { get; set; } = "master";

    /// <summary>Variant used for lint/test/build gates (usually a release variant).</summary>
    public string GateVariant { get; set; } = "Release";

    // Gates
    public bool RunLint { get; set; } = true;
    public bool LintFailsOnWarning { get; set; }
    public bool RunUnitTests { get; set; } = true;
    public bool RunInstrumentTests { get; set; }
    public bool RunSecretScan { get; set; } = true;
    public bool RunChecksumValidation { get; set; } = true;
    public bool RequireVersionCodeBump { get; set; } = true;

    // Promotion
    public bool PromoteToMain { get; set; } = true;
    public bool PushMain { get; set; } = true;
    public bool TagRelease { get; set; } = true;
    public string TagPrefix { get; set; } = "v";
    public bool BackMergeToDev { get; set; } = true;

    public bool ScheduleEnabled { get; set; }
    public string ScheduleTimes { get; set; } = "02:00";
    public string LastScheduledRunKey { get; set; } = "";

    // Firestore
    public bool PublishToFirestore { get; set; }
    public string FirestoreProjectId { get; set; } = "";
    public string FirestoreCredentialsJson { get; set; } = "";
    public string FirestoreCollection { get; set; } = "ci_runs";
}
