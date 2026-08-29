using System.Collections.Immutable;

namespace AndroidCommandCenter.Models;

/// <summary>
/// Immutable snapshot of everything a pipeline run needs. Built once, before
/// the run starts, and never mutated. Secrets are resolved into this object at
/// snapshot time so a keyring change mid-run cannot alter a running pipeline.
/// </summary>
public sealed record SigningSpec(
    string KeystorePath,
    string KeyAlias,
    string StorePassword,
    string KeyPassword)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(KeystorePath) && File.Exists(KeystorePath) &&
        !string.IsNullOrWhiteSpace(KeyAlias) &&
        !string.IsNullOrWhiteSpace(StorePassword) &&
        !string.IsNullOrWhiteSpace(KeyPassword);

    /// <summary>Never let a secret reach a log or an exception message.</summary>
    public override string ToString() =>
        $"SigningSpec {{ Keystore = {KeystorePath}, Alias = {KeyAlias}, " +
        "StorePassword = ***, KeyPassword = *** }";
}

public sealed record FirebaseSpec(
    string CliPath,
    string AppId,
    string CredentialsJsonPath,
    string Token,
    string TesterGroups,
    string TesterEmails,
    string ReleaseNotes)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(AppId) &&
        (!string.IsNullOrWhiteSpace(CredentialsJsonPath) || !string.IsNullOrWhiteSpace(Token));

    public override string ToString() =>
        $"FirebaseSpec {{ AppId = {AppId}, Creds = {CredentialsJsonPath}, Token = *** }}";
}

public sealed record GitSourceSpec(
    bool SyncBeforeRun,
    string Remote,
    string Branch,
    string Owner,
    string Repository,
    string Token)
{
    public override string ToString() =>
        $"GitSourceSpec {{ {Owner}/{Repository} {Remote}/{Branch}, Token = *** }}";
}

public sealed record PipelineRequest(
    string ProjectPath,
    string ActiveVariant,
    ImmutableArray<string> BuildVariants,
    SigningSpec Signing,
    FirebaseSpec Firebase,
    GitSourceSpec Source,
    bool RunUnitTests,
    bool RunInstrumentTests,
    TestFailureAction FailureAction)
{
    public string DeployVariant =>
        BuildVariants.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim()
        ?? ActiveVariant;

    /// <summary>Fail fast with ALL problems listed, not just the first.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(ProjectPath))
            errors.Add("No project selected.");
        else if (Platform.GradleWrapper(ProjectPath) is null)
            errors.Add($"No 'gradlew' found in {ProjectPath}.");
        if (BuildVariants.IsDefaultOrEmpty)
            errors.Add("No build variant selected.");
        if (!Signing.IsComplete)
            errors.Add(File.Exists(Signing.KeystorePath)
                ? "Signing incomplete: alias or passwords missing."
                : $"Keystore not found: '{Signing.KeystorePath}'.");
        if (!Firebase.IsComplete)
            errors.Add("Firebase incomplete: App ID and credentials JSON (or token) required.");
        if (Source.SyncBeforeRun)
        {
            if (!GitNames.IsValid(Source.Remote)) errors.Add($"Invalid git remote: '{Source.Remote}'.");
            if (!GitNames.IsValid(Source.Branch)) errors.Add($"Invalid git branch: '{Source.Branch}'.");
        }
        return errors;
    }
}