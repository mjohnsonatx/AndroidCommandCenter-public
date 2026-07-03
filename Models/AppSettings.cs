using System.IO;
using System.Text.Json;

namespace AndroidCommandCenter.Models;

public class AppSettings
{
    public List<string> ProjectPaths { get; set; } = new();
    public string? LastSelectedProject { get; set; }
    public string? PreferredAvd { get; set; }

    // Per-project: projectPath → variant list
    public Dictionary<string, List<string>> ProjectVariants { get; set; } = new();
    // Per-project: projectPath → last selected variant
    public Dictionary<string, string> SelectedVariants { get; set; } = new();
    // Per-project: projectPath → device serials authorized to run instrument tests.
    // connectedAndroidTest ends by uninstalling the app (wiping its data on the
    // device), so this list is the guard rail that keeps test runs off phones that
    // hold data worth keeping. Empty/missing list = no restriction. Emulators are
    // always allowed - their data is disposable.
    public Dictionary<string, List<string>> AuthorizedTestDevices { get; set; } = new();

    private static string FilePath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "acc_settings.json");

    public CiCdConfig CiCd { get; set; } = new();
    public AutomationConfig Automation { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ── Variant helpers ──────────────────────────

    public List<string> GetVariants(string project) =>
        ProjectVariants.TryGetValue(project, out var v) ? v : ["Debug", "Release"];

    public string GetSelectedVariant(string project) =>
        SelectedVariants.TryGetValue(project, out var v) ? v : "Debug";

    public void SetVariants(string project, List<string> variants)
    {
        ProjectVariants[project] = variants;
        Save();
    }

    public void SetSelectedVariant(string project, string variant)
    {
        SelectedVariants[project] = variant;
        Save();
    }

    // ── Authorized test device helpers ───────────

    public List<string> GetAuthorizedTestDevices(string project) =>
        AuthorizedTestDevices.TryGetValue(project, out var v) ? v : [];

    public bool AddAuthorizedTestDevice(string project, string serial)
    {
        if (!AuthorizedTestDevices.TryGetValue(project, out var list))
            AuthorizedTestDevices[project] = list = [];
        if (list.Contains(serial, StringComparer.OrdinalIgnoreCase)) return false;
        list.Add(serial);
        Save();
        return true;
    }

    public bool RemoveAuthorizedTestDevice(string project, string serial)
    {
        if (!AuthorizedTestDevices.TryGetValue(project, out var list)) return false;
        int removed = list.RemoveAll(s => string.Equals(s, serial, StringComparison.OrdinalIgnoreCase));
        if (removed > 0) Save();
        return removed > 0;
    }
}
