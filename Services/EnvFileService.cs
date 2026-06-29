using System.IO;

namespace AndroidCommandCenter.Services;

public class EnvFileService
{
    private readonly string _path;
    private readonly Dictionary<string, string> _vars = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? LogMessage;

    public EnvFileService(string? path = null)
    {
        _path = path ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env");
        Reload();
    }

    // ── read / write ────────────────────────────
    public void Reload()
    {
        _vars.Clear();
        if (!File.Exists(_path)) return;
        foreach (var raw in File.ReadAllLines(_path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim().Trim('"', '\'');
            _vars[key] = val;
        }
        LogMessage?.Invoke($"[env] loaded {_vars.Count} vars from {_path}");
    }

    public void Save()
    {
        var lines = new List<string>();
        if (File.Exists(_path))
        {
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in File.ReadAllLines(_path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') { lines.Add(raw); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) { lines.Add(raw); continue; }
                string key = line[..eq].Trim();
                if (_vars.TryGetValue(key, out var val))
                { lines.Add($"{key}={val}"); written.Add(key); }
                else lines.Add(raw);
            }
            foreach (var kv in _vars)
                if (!written.Contains(kv.Key)) lines.Add($"{kv.Key}={kv.Value}");
        }
        else
        {
            lines.Add("# Android Command Center – secrets");
            lines.Add("# Add this file to .gitignore");
            lines.Add("");
            foreach (var kv in _vars) lines.Add($"{kv.Key}={kv.Value}");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllLines(_path, lines);
        LogMessage?.Invoke("[env] saved.");
    }

    public string Get(string key) =>
        _vars.TryGetValue(key, out var v) ? v : "";

    public void Set(string key, string value) => _vars[key] = value;

    /// <summary>Return env value if present, else fallback.</summary>
    public string Resolve(string envKey, string fallback)
    {
        string v = Get(envKey);
        return !string.IsNullOrWhiteSpace(v) ? v : fallback;
    }

    // ── convenience properties ──────────────────
    public string GitHubToken { get => Get("ACC_GITHUB_TOKEN"); set => Set("ACC_GITHUB_TOKEN", value); }
    public string GitHubOwner { get => Get("ACC_GITHUB_OWNER"); set => Set("ACC_GITHUB_OWNER", value); }
    public string GitHubRepo { get => Get("ACC_GITHUB_REPO"); set => Set("ACC_GITHUB_REPO", value); }
    public string FirebaseAppId { get => Get("ACC_FIREBASE_APP_ID"); set => Set("ACC_FIREBASE_APP_ID", value); }
    public string FirebaseCredentials { get => Get("ACC_FIREBASE_CREDS_JSON"); set => Set("ACC_FIREBASE_CREDS_JSON", value); }
    public string FirebaseCliPath { get => Get("ACC_FIREBASE_CLI"); set => Set("ACC_FIREBASE_CLI", value); }
    public string KeystorePath { get => Get("ACC_KS_PATH"); set => Set("ACC_KS_PATH", value); }
    public string StorePassword { get => Get("ACC_KS_STORE_PASS"); set => Set("ACC_KS_STORE_PASS", value); }
    public string KeyAlias { get => Get("ACC_KS_KEY_ALIAS"); set => Set("ACC_KS_KEY_ALIAS", value); }
    public string KeyPassword { get => Get("ACC_KS_KEY_PASS"); set => Set("ACC_KS_KEY_PASS", value); }
    public string SmtpHost { get => Get("ACC_SMTP_HOST"); set => Set("ACC_SMTP_HOST", value); }
    public string SmtpPort { get => Get("ACC_SMTP_PORT"); set => Set("ACC_SMTP_PORT", value); }
    public string SmtpUser { get => Get("ACC_SMTP_USER"); set => Set("ACC_SMTP_USER", value); }
    public string SmtpPassword { get => Get("ACC_SMTP_PASS"); set => Set("ACC_SMTP_PASS", value); }
}