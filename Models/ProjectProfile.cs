namespace AndroidCommandCenter.Models;

/// <summary>
/// Per-project credential and config profile.
/// Keyed by a normalized project path.
/// </summary>
public class ProjectProfile
{
    public string ProjectPath { get; set; } = "";
    public string DisplayName { get; set; } = "";

    // GitHub source
    public string GitHubOwner { get; set; } = "";
    public string GitHubRepo { get; set; } = "";
    public string GitHubToken { get; set; } = "";
    public string DefaultRemote { get; set; } = "origin";
    public string DefaultBranch { get; set; } = "master";

    // Firebase
    public string FirebaseAppId { get; set; } = "";
    public string FirebaseCredentialsJson { get; set; } = "";
    public string FirebaseCliPath { get; set; } = "firebase";

    // Signing
    public string KeystorePath { get; set; } = "";
    public string KeyAlias { get; set; } = "";
    public string StorePassword { get; set; } = "";
    public string KeyPassword { get; set; } = "";

    /// <summary>Stable key for lookups — normalized lowercase path.</summary>
    public string Key => ProjectPath.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    public bool HasFirebase =>
        !string.IsNullOrWhiteSpace(FirebaseAppId) &&
        !string.IsNullOrWhiteSpace(FirebaseCredentialsJson);

    public bool HasSigning =>
        !string.IsNullOrWhiteSpace(KeystorePath) &&
        !string.IsNullOrWhiteSpace(KeyAlias) &&
        !string.IsNullOrWhiteSpace(StorePassword) &&
        !string.IsNullOrWhiteSpace(KeyPassword);
}