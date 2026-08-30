using System.Text.Json;
using Google.Cloud.Firestore;

namespace AndroidCommandCenter.Ci;

/// <summary>
/// Writes run manifests to Firestore. Documents are the manifest JSON
/// round-tripped through Dictionary so Firestore gets native types (numbers,
/// arrays, nested maps) instead of one giant string — which is what lets you
/// write alert rules on `success == false` or `stages[*].status == "Failed"`.
/// </summary>
public sealed class FirestoreSink
{
    private readonly string _projectId;
    private readonly string _credentialsPath;
    private readonly string _collection;
    private FirestoreDb? _db;

    public FirestoreSink(string projectId, string credentialsPath, string collection)
    {
        _projectId = projectId; _credentialsPath = credentialsPath; _collection = collection;
    }

    /// <summary>Reads project_id from the service-account JSON when not configured.</summary>
    public static string? ProjectIdFromCredentials(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("project_id", out var p) ? p.GetString() : null;
        }
        catch { return null; }
    }

    private async Task<FirestoreDb> Db(CancellationToken ct)
    {
        if (_db != null) return _db;
        if (!File.Exists(_credentialsPath))
            throw new FileNotFoundException("Firestore service-account JSON not found", _credentialsPath);
        string pid = string.IsNullOrWhiteSpace(_projectId)
            ? ProjectIdFromCredentials(_credentialsPath) ?? throw new InvalidOperationException("No Firestore project id.")
            : _projectId;
        _db = await new FirestoreDbBuilder { ProjectId = pid, CredentialsPath = _credentialsPath }.BuildAsync(ct);
        return _db;
    }

    public async Task PublishAsync(CiRunManifest manifest, CancellationToken ct)
    {
        var db = await Db(ct);
        var data = ToFirestore(manifest);
        data["publishedAt"] = FieldValue.ServerTimestamp;
        // Denormalized top-level fields for cheap queries/alerts
        data["failedStageName"] = manifest.FailedStage ?? "";
        data["hasWarnings"] = manifest.Stages.Any(s => s.Status == StageStatus.Warning);

        await db.Collection(_collection).Document(manifest.RunId).SetAsync(data, SetOptions.Overwrite, ct);

        // A single "latest" doc per project makes dashboards trivial.
        await db.Collection(_collection).Document($"_latest_{Slug(manifest.Project)}")
            .SetAsync(new Dictionary<string, object?>
            {
                ["runId"] = manifest.RunId, ["success"] = manifest.Success, ["result"] = manifest.Result,
                ["tag"] = manifest.Tag, ["mergeSha"] = manifest.MergeSha, ["finishedAt"] = manifest.FinishedAt,
                ["project"] = manifest.Project, ["publishedAt"] = FieldValue.ServerTimestamp
            }, SetOptions.Overwrite, ct);
    }

    public async Task<bool> ProbeAsync(CancellationToken ct)
    {
        var db = await Db(ct);
        await db.Collection(_collection).Document("_probe").SetAsync(
            new { at = FieldValue.ServerTimestamp, host = Environment.MachineName }, SetOptions.MergeAll, ct);
        return true;
    }

    private static readonly JsonSerializerOptions J = new()
    { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static Dictionary<string, object?> ToFirestore(object o)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(o, J));
        return (Dictionary<string, object?>)Convert(doc.RootElement)!;
    }

    private static object? Convert(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Convert(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(Convert).ToList(),
        JsonValueKind.String => DateTime.TryParse(e.GetString(), null,
                                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt) && e.GetString()!.Contains('T')
                                ? dt.ToUniversalTime() : e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static string Slug(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}