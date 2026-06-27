using System.IO;
using System.Text.Json;

namespace AndroidCommandCenter.Models;

public class AppSettings
{
    public List<string> ProjectPaths { get; set; } = new();
    public string? LastSelectedProject { get; set; }
    public string? PreferredAvd { get; set; }

    private static string FilePath =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "acc_settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, opts));
        }
        catch { }
    }
}