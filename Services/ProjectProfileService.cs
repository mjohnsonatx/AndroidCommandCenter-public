using System.IO;
using System.Text.Json;
using AndroidCommandCenter.Models;

namespace AndroidCommandCenter.Services;

public class ProjectProfileService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "AndroidCommandCenter");
    private static readonly string FilePath = Path.Combine(Dir, "project_profiles.json");

    private Dictionary<string, ProjectProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    public event Action<string>? LogMessage;

    public ProjectProfileService()
    {
        Directory.CreateDirectory(Dir);
        Load();
    }

    private void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<ProjectProfile>>(
                File.ReadAllText(FilePath)) ?? [];
            _profiles = list.ToDictionary(p => p.Key, p => p, StringComparer.OrdinalIgnoreCase);
        }
        catch { _profiles = new(StringComparer.OrdinalIgnoreCase); }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(
            _profiles.Values.ToList(),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }

    private static string NormKey(string path) =>
        path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    public ProjectProfile GetOrCreate(string projectPath)
    {
        string key = NormKey(projectPath);
        if (_profiles.TryGetValue(key, out var existing))
            return existing;

        var profile = new ProjectProfile
        {
            ProjectPath = projectPath,
            DisplayName = Path.GetFileName(projectPath.TrimEnd('\\', '/'))
        };
        _profiles[key] = profile;
        Save();
        return profile;
    }

    public void SaveProfile(ProjectProfile profile)
    {
        _profiles[profile.Key] = profile;
        Save();
        LogMessage?.Invoke($"[profile] saved for {profile.DisplayName}");
    }

    /// <summary>
    /// Fill empty fields from .env defaults, return the merged result.
    /// Does NOT write back to the profile — caller decides when to persist.
    /// </summary>
    public static ProjectProfile MergeWithEnv(ProjectProfile profile, EnvFileService env)
    {
        if (string.IsNullOrWhiteSpace(profile.GitHubOwner))
            profile.GitHubOwner = env.GitHubOwner;
        if (string.IsNullOrWhiteSpace(profile.GitHubRepo))
            profile.GitHubRepo = env.GitHubRepo;
        if (string.IsNullOrWhiteSpace(profile.GitHubToken))
            profile.GitHubToken = env.GitHubToken;
        if (string.IsNullOrWhiteSpace(profile.FirebaseAppId))
            profile.FirebaseAppId = env.FirebaseAppId;
        if (string.IsNullOrWhiteSpace(profile.FirebaseCredentialsJson))
            profile.FirebaseCredentialsJson = env.FirebaseCredentials;
        if (string.IsNullOrWhiteSpace(profile.FirebaseCliPath) || profile.FirebaseCliPath == "firebase")
            profile.FirebaseCliPath = string.IsNullOrWhiteSpace(env.FirebaseCliPath) ? "firebase" : env.FirebaseCliPath;
        if (string.IsNullOrWhiteSpace(profile.KeystorePath))
            profile.KeystorePath = env.KeystorePath;
        if (string.IsNullOrWhiteSpace(profile.KeyAlias))
            profile.KeyAlias = env.KeyAlias;
        if (string.IsNullOrWhiteSpace(profile.StorePassword))
            profile.StorePassword = env.StorePassword;
        if (string.IsNullOrWhiteSpace(profile.KeyPassword))
            profile.KeyPassword = env.KeyPassword;
        return profile;
    }

    public IReadOnlyList<ProjectProfile> AllProfiles => _profiles.Values.ToList();
}