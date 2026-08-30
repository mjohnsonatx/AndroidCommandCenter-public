using System.Text.Json.Serialization;

namespace AndroidCommandCenter.Models;

public enum TestFailureAction
{
    AutomaticBounceBack,  // stop the pipeline immediately
    Warning,              // show details, user chooses continue/stop
    WarningWithConfirm,   // show details, require checkbox to continue
    SilentFailure         // log and continue, no prompt
}

/// <summary>
/// Non-secret pipeline configuration. Secrets live in SecretStore, keyed by
/// "&lt;profileKey&gt;/&lt;name&gt;" — nothing sensitive is serialized here.
/// The Legacy* properties exist only to detect unreadable Windows DPAPI blobs
/// from the pre-migration settings file.
/// </summary>
public sealed class CiCdConfig
{
    public string PipelineRemoteName { get; set; } = "origin";
    public string PipelineBranch { get; set; } = "main";
    public bool SyncBranchBeforePipeline { get; set; }

    public List<string> BuildVariants { get; set; } = new();

    public string TesterGroups { get; set; } = "testers";
    public string TesterEmails { get; set; } = "";
    public string ReleaseNotes { get; set; } = "New build";

    public bool RunUnitTests { get; set; } = true;
    public bool RunInstrumentTests { get; set; } = true;
    public TestFailureAction FailureAction { get; set; } = TestFailureAction.AutomaticBounceBack;

    [JsonPropertyName("EncryptedStorePassword")] public string? LegacyEncryptedStorePassword { get; set; }
    [JsonPropertyName("EncryptedKeyPassword")]   public string? LegacyEncryptedKeyPassword { get; set; }
    [JsonPropertyName("EncryptedGitHubToken")]   public string? LegacyEncryptedGitHubToken { get; set; }
    [JsonPropertyName("EncryptedFirebaseToken")] public string? LegacyEncryptedFirebaseToken { get; set; }
}