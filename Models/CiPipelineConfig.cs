namespace AndroidCommandCenter.Models;

/// <summary>
/// The local CI/CD promotion pipeline: dev → staging → production, then
/// advance staging to the exact release and back-merge production → dev.
/// Merges are --no-ff so every release is one
/// revertable commit.
/// </summary>
public sealed class CiPipelineConfig
{
    public const string ProductionReleaseVariant = "ProductionRelease";

    public string Remote { get; set; } = "origin";
    public string DevBranch { get; set; } = "dev";
    public string StagingBranch { get; set; } = "staging";
    public string MainBranch { get; set; } = "master";

    /// <summary>Production variant used for lint and the build gate.</summary>
    public string GateVariant { get; set; } = ProductionReleaseVariant;

    /// <summary>
    /// Production variant used for unit and instrument tests. Promotion CI
    /// enforces this value even when older saved settings contain another variant.
    /// </summary>
    public string TestVariant { get; set; } = ProductionReleaseVariant;

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
    /// <summary>Atomically publish production, staging, and the optional tag; then push dev's back-merge.</summary>
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
