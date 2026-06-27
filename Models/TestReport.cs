namespace AndroidCommandCenter.Models;

public enum TestStatus { Passed, Failed, Error, Skipped }

public class TestCaseResult
{
    public string ClassName { get; set; } = "";
    public string Name { get; set; } = "";
    public double TimeSeconds { get; set; }
    public TestStatus Status { get; set; }
    public string? FailureType { get; set; }
    public string? FailureMessage { get; set; }
    public string? StackTrace { get; set; }
}

public class TestReport
{
    public string TestType { get; set; } = "";
    public List<TestCaseResult> Cases { get; set; } = new();

    public int Total => Cases.Count;
    public int Passed => Cases.Count(c => c.Status == TestStatus.Passed);
    public int Failed => Cases.Count(c => c.Status is TestStatus.Failed or TestStatus.Error);
    public int Skipped => Cases.Count(c => c.Status == TestStatus.Skipped);
    public double TotalTimeSeconds => Cases.Sum(c => c.TimeSeconds);

    public List<TestCaseResult> Failures =>
        Cases.Where(c => c.Status is TestStatus.Failed or TestStatus.Error).ToList();
}