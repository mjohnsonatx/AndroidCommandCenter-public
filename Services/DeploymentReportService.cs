using System.IO;
using System.Text.Json;
using AndroidCommandCenter.Models;

namespace AndroidCommandCenter.Services;

public class DeploymentReportService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "AndroidCommandCenter");
    private static readonly string File_ = Path.Combine(Dir, "deployment_history.json");

    private List<DeploymentRecord> _records = [];
    public DeploymentReportService() { Directory.CreateDirectory(Dir); Load(); }

    private void Load()
    {
        if (!File.Exists(File_)) return;
        try
        {
            _records = JsonSerializer.Deserialize<List<DeploymentRecord>>(
                  File.ReadAllText(File_)) ?? [];
        }
        catch { _records = []; }
    }
    private void Save() =>
        File.WriteAllText(File_,
            JsonSerializer.Serialize(_records, new JsonSerializerOptions { WriteIndented = true }));

    public IReadOnlyList<DeploymentRecord> All => _records;

    public void Add(DeploymentRecord r) { _records.Insert(0, r); Save(); }

    public (int total, int ok, int fail) Stats()
    {
        int t = _records.Count, ok = _records.Count(r => r.Success);
        return (t, ok, t - ok);
    }

    /// <summary>Build a record from pipeline results + optional test reports.</summary>
    public DeploymentRecord CreateRecord(
        string project, string variant, string branch,
        bool success, string status, string logPath,
        TestReport? unit = null, TestReport? instr = null)
    {
        var r = new DeploymentRecord
        {
            ProjectName = project,
            Variant = variant,
            Branch = branch,
            Success = success,
            Status = status,
            LogPath = logPath
        };
        void Map(TestReport rpt, string type)
        {
            var s = new TestSummaryRecord
            { Total = rpt.Total, Passed = rpt.Passed, Failed = rpt.Failed, Skipped = rpt.Skipped };
            if (type == "Unit") r.UnitTests = s; else r.InstrumentTests = s;
            foreach (var f in rpt.Failures)
                r.Failures.Add(new FailedTestRecord
                {
                    TestType = type,
                    ClassName = f.ClassName,
                    TestName = f.Name,
                    Message = f.FailureMessage ?? ""
                });
        }
        if (unit is { Total: > 0 }) Map(unit, "Unit");
        if (instr is { Total: > 0 }) Map(instr, "Instrument");
        return r;
    }
}
