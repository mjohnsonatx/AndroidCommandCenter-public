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

    private static string FilePath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "acc_settings.json");

    public CiCdConfig CiCd { get; set; } = new();

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
}