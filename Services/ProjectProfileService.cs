using System.IO;
using System.Text.Json;
using AndroidCommandCenter.Models;

namespace AndroidCommandCenter.Services;

public sealed class ProjectProfileService
{
    private Dictionary<string, ProjectProfile> _profiles = new(StringComparer.Ordinal);
    private readonly SecretStore _secrets;
    public event Action<string>? LogMessage;

    public ProjectProfileService(SecretStore secrets)
    {
        _secrets = secrets;
        Load();
    }

    private void Load()
    {
        var file = Paths.ProfilesFile;
        if (!File.Exists(file)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<ProjectProfile>>(File.ReadAllText(file)) ?? [];
            _profiles = list
                .Where(p => !string.IsNullOrWhiteSpace(p.ProjectPath))
                .GroupBy(p => p.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            LogMessage?.Invoke($"[profile] loaded {_profiles.Count} project profile(s)");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[profile] could not read profiles: {ex.Message}");
            _profiles = new(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            var tmp = Paths.ProfilesFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                _profiles.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, Paths.ProfilesFile, overwrite: true);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"[profile] SAVE FAILED: {ex.Message}");
            throw;   // caller toasts it — a silent failure loses credentials
        }
    }

    public ProjectProfile GetOrCreate(string projectPath)
    {
        string key = ProjectProfile.NormalizeKey(projectPath);
        if (_profiles.TryGetValue(key, out var existing)) return existing;

        var profile = new ProjectProfile
        {
            ProjectPath = projectPath,
            DisplayName = Path.GetFileName(projectPath.TrimEnd('/', '\\')),
            DefaultBranch = "main"
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

    /// <summary>True when keystore paths AND both passwords are present.</summary>
    public bool HasCompleteSigning(ProjectProfile p) =>
        p.HasSigningPaths &&
        File.Exists(p.KeystorePath) &&
        _secrets.Get($"{p.Key}/store-password").Length > 0 &&
        _secrets.Get($"{p.Key}/key-password").Length > 0;

    public SigningSpec SigningFor(ProjectProfile p) => new(
        p.KeystorePath, p.KeyAlias,
        _secrets.Get($"{p.Key}/store-password"),
        _secrets.Get($"{p.Key}/key-password"));

    public string GitHubToken(ProjectProfile p) => _secrets.Get($"{p.Key}/github-token");
    public void SetGitHubToken(ProjectProfile p, string token) =>
        _secrets.Set($"{p.Key}/github-token", token);

    /// <summary>Fill empty fields from .env defaults. Does not persist.</summary>
    public ProjectProfile MergeWithEnv(ProjectProfile p, EnvFileService env)
    {
        if (string.IsNullOrWhiteSpace(p.GitHubOwner)) p.GitHubOwner = env.GitHubOwner;
        if (string.IsNullOrWhiteSpace(p.GitHubRepo)) p.GitHubRepo = env.GitHubRepo;
        if (string.IsNullOrWhiteSpace(p.FirebaseAppId)) p.FirebaseAppId = env.FirebaseAppId;
        if (string.IsNullOrWhiteSpace(p.FirebaseCredentialsJson)) p.FirebaseCredentialsJson = env.FirebaseCredentials;
        if (string.IsNullOrWhiteSpace(p.FirebaseCliPath) || p.FirebaseCliPath == "firebase")
            p.FirebaseCliPath = string.IsNullOrWhiteSpace(env.FirebaseCliPath) ? "firebase" : env.FirebaseCliPath;
        if (string.IsNullOrWhiteSpace(p.KeystorePath)) p.KeystorePath = env.KeystorePath;
        if (string.IsNullOrWhiteSpace(p.KeyAlias)) p.KeyAlias = env.KeyAlias;

        // Secrets: only seed the keyring from .env when the keyring is empty,
        // so .env can bootstrap a fresh machine without ever overwriting.
        Seed($"{p.Key}/github-token", env.GitHubToken);
        Seed($"{p.Key}/store-password", env.StorePassword);
        Seed($"{p.Key}/key-password", env.KeyPassword);
        return p;

        void Seed(string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && _secrets.Get(key).Length == 0)
            {
                try { _secrets.Set(key, value); }
                catch (Exception ex) { LogMessage?.Invoke($"[profile] could not seed {key}: {ex.Message}"); }
            }
        }
    }

    public IReadOnlyList<ProjectProfile> AllProfiles => _profiles.Values.ToList();
}