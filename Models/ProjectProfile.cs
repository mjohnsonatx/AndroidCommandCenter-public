using System.IO;

namespace AndroidCommandCenter.Models;

public sealed class ProjectProfile
{
    public string ProjectPath { get; set; } = "";
    public string DisplayName { get; set; } = "";

    public string GitHubOwner { get; set; } = "";
    public string GitHubRepo { get; set; } = "";
    public string DefaultRemote { get; set; } = "origin";
    public string DefaultBranch { get; set; } = "main";

    public string FirebaseAppId { get; set; } = "";
    public string FirebaseCredentialsJson { get; set; } = "";
    public string FirebaseCliPath { get; set; } = "firebase";

    public string KeystorePath { get; set; } = "";
    public string KeyAlias { get; set; } = "";

    /// <summary>
    /// Stable lookup key and secret namespace. The old version did
    /// Replace('/','\\') + ToLowerInvariant(), which on a case-sensitive Linux
    /// filesystem merges genuinely distinct paths (~/Work and ~/work) and
    /// mangles every separator. Normalise properly and keep case.
    /// </summary>
    public string Key => NormalizeKey(ProjectPath);

    public static string NormalizeKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { path = Path.GetFullPath(path); } catch { }
        path = path.Replace('\\', '/').TrimEnd('/');
        return Platform.IsWindows ? path.ToLowerInvariant() : path;
    }

    public bool HasFirebase =>
        !string.IsNullOrWhiteSpace(FirebaseAppId) &&
        !string.IsNullOrWhiteSpace(FirebaseCredentialsJson);

    /// <summary>Signing needs a secret lookup, so the service decides this.</summary>
    public bool HasSigningPaths =>
        !string.IsNullOrWhiteSpace(KeystorePath) && !string.IsNullOrWhiteSpace(KeyAlias);
}