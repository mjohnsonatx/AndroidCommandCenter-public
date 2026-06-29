namespace AndroidCommandCenter.Models;

public class DeploymentRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string ProjectName { get; set; } = "";
    public string Variant { get; set; } = "";
    public string Branch { get; set; } = "";
    public bool Success { get; set; }
    public string Status { get; set; } = "";
    public string LogPath { get; set; } = "";
    public TestSummaryRecord? UnitTests { get; set; }
    public TestSummaryRecord? InstrumentTests { get; set; }
    public List<FailedTestRecord> Failures { get; set; } = [];

    // display helpers
    public string TimestampDisplay => Timestamp.ToString("yyyy-MM-dd HH:mm");
    public string Result => Success ? "✓ OK" : "✗ FAIL";
}

public class TestSummaryRecord
{
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public override string ToString() => $"{Passed}/{Total}";
}

public class FailedTestRecord
{
    public string TestType { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string TestName { get; set; } = "";
    public string Message { get; set; } = "";
}