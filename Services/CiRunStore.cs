using System.IO;
using System.Text.Json;
using AndroidCommandCenter.Models;

namespace AndroidCommandCenter.Services;

/// <summary>Local persistence for run manifests. One file per run, newest first.</summary>
public sealed class CiRunStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public string Save(CiRunManifest m)
    {
        string path = Path.Combine(Paths.CiRuns, $"{m.RunId}.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(m, Opts));
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    public IReadOnlyList<CiRunManifest> All(int limit = 200) =>
        Directory.Exists(Paths.CiRuns)
            ? Directory.GetFiles(Paths.CiRuns, "*.json")
                .OrderByDescending(f => f)
                .Take(limit)
                .Select(Load)
                .OfType<CiRunManifest>()
                .ToList()
            : [];

    public CiRunManifest? Load(string path)
    {
        try { return JsonSerializer.Deserialize<CiRunManifest>(File.ReadAllText(path), Opts); }
        catch { return null; }
    }

    public CiRunManifest? ById(string runId)
    {
        string p = Path.Combine(Paths.CiRuns, $"{runId}.json");
        return File.Exists(p) ? Load(p) : null;
    }

    /// <summary>Most recent run that is still revertable.</summary>
    public CiRunManifest? LastRollbackable(string projectName) =>
        All().FirstOrDefault(r =>
            r.ProjectName == projectName && r.CanRollback);
}