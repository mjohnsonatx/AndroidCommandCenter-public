using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AndroidCommandCenter.Models;

public sealed class AppSettings
{
    public List<string> ProjectPaths { get; set; } = new();
    public string? LastSelectedProject { get; set; }
    public string? PreferredAvd { get; set; }

    public Dictionary<string, List<string>> ProjectVariants { get; set; } = new();
    public Dictionary<string, string> SelectedVariants { get; set; } = new();

    // Per-project serials allowed to run instrument tests. connectedAndroidTest
    // ends by uninstalling the app (wiping its data), so this is the guard rail
    // that keeps test runs off phones holding data worth keeping. Empty = no
    // restriction. Emulators are always allowed — their data is disposable.
    public Dictionary<string, List<string>> AuthorizedTestDevices { get; set; } = new();

    public CiCdConfig CiCd { get; set; } = new();
    public AutomationConfig Automation { get; set; } = new();
    public CiPipelineConfig Pipeline { get; set; } = new();

    /// <summary>Set when a load found Windows DPAPI blobs that can't be read here.</summary>
    [JsonIgnore] public bool NeedsCredentialReentry { get; private set; }
    [JsonIgnore] public string? LoadError { get; private set; }

    public static AppSettings Load(Action<string>? log = null)
    {
        Paths.MigrateLegacy(log);
        var file = Paths.SettingsFile;
        try
        {
            if (!File.Exists(file)) return new();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file), JsonOpts) ?? new();
            s.NeedsCredentialReentry = s.DetectOrphanedDpapi();
            return s;
        }
        catch (Exception ex)
        {
            // Never start with a blank config silently — that looks like data
            // loss. Preserve the bad file and report it.
            try
            {
                var bak = file + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                File.Copy(file, bak, overwrite: true);
                log?.Invoke($"[settings] unreadable; preserved as {bak}");
                return new() { LoadError = $"Settings file was unreadable ({ex.Message}). " +
                                           $"A backup was kept at {bak} and defaults were loaded." };
            }
            catch
            {
                return new() { LoadError = $"Settings file was unreadable: {ex.Message}" };
            }
        }
    }

    /// <summary>
    /// Legacy DPAPI ciphertext cannot be decrypted off the original Windows
    /// user account. Detect it so the UI can ask for re-entry rather than
    /// handing the build a silently empty password.
    /// </summary>
    private bool DetectOrphanedDpapi() =>
        !string.IsNullOrEmpty(CiCd.LegacyEncryptedStorePassword) ||
        !string.IsNullOrEmpty(CiCd.LegacyEncryptedKeyPassword) ||
        !string.IsNullOrEmpty(CiCd.LegacyEncryptedGitHubToken) ||
        !string.IsNullOrEmpty(Automation.Email.LegacyEncryptedPassword);

    public void ClearLegacyCredentials()
    {
        CiCd.LegacyEncryptedStorePassword = null;
        CiCd.LegacyEncryptedKeyPassword = null;
        CiCd.LegacyEncryptedGitHubToken = null;
        CiCd.LegacyEncryptedFirebaseToken = null;
        Automation.Email.LegacyEncryptedPassword = null;
        NeedsCredentialReentry = false;
        Save();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Atomic save — a crash mid-write must not destroy the config.</summary>
    public void Save()
    {
        var file = Paths.SettingsFile;
        try
        {
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
            File.Move(tmp, file, overwrite: true);
        }
        catch { /* caller already has a toast path for this */ }
    }

    // ── Variant helpers ────────────────────────────────────────────────
    public List<string> GetVariants(string project) =>
        ProjectVariants.TryGetValue(project, out var v) && v.Count > 0 ? v : ["Debug", "Release"];

    public string GetSelectedVariant(string project) =>
        SelectedVariants.TryGetValue(project, out var v) ? v : "Debug";

    public void SetVariants(string project, List<string> variants)
    { ProjectVariants[project] = variants; Save(); }

    public void SetSelectedVariant(string project, string variant)
    { SelectedVariants[project] = variant; Save(); }

    // ── Authorized test devices ────────────────────────────────────────
    public List<string> GetAuthorizedTestDevices(string project) =>
        AuthorizedTestDevices.TryGetValue(project, out var v) ? v : [];

    public bool AddAuthorizedTestDevice(string project, string serial)
    {
        if (!AuthorizedTestDevices.TryGetValue(project, out var list))
            AuthorizedTestDevices[project] = list = [];
        if (list.Contains(serial, StringComparer.OrdinalIgnoreCase)) return false;
        list.Add(serial); Save(); return true;
    }

    public bool RemoveAuthorizedTestDevice(string project, string serial)
    {
        if (!AuthorizedTestDevices.TryGetValue(project, out var list)) return false;
        int n = list.RemoveAll(s => string.Equals(s, serial, StringComparison.OrdinalIgnoreCase));
        if (n > 0) Save();
        return n > 0;
    }
}