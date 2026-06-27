using AndroidCommandCenter.Models;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace AndroidCommandCenter.Services;

public class TestReportService
{
    public event Action<string>? LogMessage;

    public static string ReportsDirectory
    {
        get
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestReports");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // ── Parse all JUnit XML result files for a test run ──
    public TestReport ParseResults(string projectPath, string testType)
    {
        var report = new TestReport { TestType = testType };
        var files = FindResultFiles(projectPath, testType);

        if (files.Count == 0)
        {
            LogMessage?.Invoke("No test result XML files found " +
                               "(tests may not have run, or were UP-TO-DATE).");
            return report;
        }

        LogMessage?.Invoke($"Parsing {files.Count} result file(s)...");
        foreach (var f in files)
        {
            try { ParseXml(f, report); }
            catch (Exception ex) { LogMessage?.Invoke($"[WARN] {Path.GetFileName(f)}: {ex.Message}"); }
        }
        return report;
    }

    private static List<string> FindResultFiles(string projectPath, string testType)
    {
        var roots = new List<string>();
        bool isUnit = testType.Contains("Unit", StringComparison.OrdinalIgnoreCase);

        foreach (var module in GetModuleDirs(projectPath))
        {
            roots.Add(isUnit
                ? Path.Combine(module, "build", "test-results")
                : Path.Combine(module, "build", "outputs", "androidTest-results"));
        }

        var files = new List<string>();
        foreach (var root in roots.Where(Directory.Exists))
            files.AddRange(Directory.GetFiles(root, "*.xml", SearchOption.AllDirectories));

        return files.Distinct().ToList();
    }

    private static IEnumerable<string> GetModuleDirs(string projectPath)
    {
        yield return projectPath;
        foreach (var dir in Directory.GetDirectories(projectPath))
            if (!Path.GetFileName(dir).StartsWith("."))
                yield return dir;
    }

    private static void ParseXml(string file, TestReport report)
    {
        var doc = XDocument.Load(file);

        foreach (var suite in doc.Descendants("testsuite"))
            foreach (var tc in suite.Elements("testcase"))
            {
                var r = new TestCaseResult
                {
                    ClassName = tc.Attribute("classname")?.Value ?? "",
                    Name = tc.Attribute("name")?.Value ?? "",
                    TimeSeconds = double.TryParse(tc.Attribute("time")?.Value,
                        CultureInfo.InvariantCulture, out var t) ? t : 0
                };

                var failure = tc.Element("failure");
                var error = tc.Element("error");
                var skipped = tc.Element("skipped");

                if (failure != null)
                {
                    r.Status = TestStatus.Failed;
                    r.FailureType = failure.Attribute("type")?.Value;
                    r.FailureMessage = failure.Attribute("message")?.Value;
                    r.StackTrace = failure.Value;
                }
                else if (error != null)
                {
                    r.Status = TestStatus.Error;
                    r.FailureType = error.Attribute("type")?.Value;
                    r.FailureMessage = error.Attribute("message")?.Value;
                    r.StackTrace = error.Value;
                }
                else if (skipped != null)
                {
                    r.Status = TestStatus.Skipped;
                }
                else
                {
                    r.Status = TestStatus.Passed;
                }

                report.Cases.Add(r);
            }
    }

    // ── Save a human-readable report + raw log to disk ──
    public string SaveReport(TestReport report, string projectPath, string variant, string rawLog)
    {
        string project = Path.GetFileName(projectPath.TrimEnd('\\', '/'));
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string fileName = $"{project}_{report.TestType}_{variant}_{timestamp}.txt".Replace(" ", "");
        string path = Path.Combine(ReportsDirectory, fileName);

        var sb = new StringBuilder();
        sb.AppendLine("========================================");
        sb.AppendLine("  TEST REPORT");
        sb.AppendLine("========================================");
        sb.AppendLine($"Project:  {project}");
        sb.AppendLine($"Type:     {report.TestType}");
        sb.AppendLine($"Variant:  {variant}");
        sb.AppendLine($"Date:     {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("SUMMARY");
        sb.AppendLine($"  Total:   {report.Total}");
        sb.AppendLine($"  Passed:  {report.Passed}");
        sb.AppendLine($"  Failed:  {report.Failed}");
        sb.AppendLine($"  Skipped: {report.Skipped}");
        sb.AppendLine($"  Time:    {report.TotalTimeSeconds:F2}s");
        sb.AppendLine();

        if (report.Failures.Count > 0)
        {
            sb.AppendLine("FAILURES");
            sb.AppendLine("----------------------------------------");
            int i = 1;
            foreach (var f in report.Failures)
            {
                sb.AppendLine($"[{i}] {f.ClassName}.{f.Name}");
                if (f.FailureType != null) sb.AppendLine($"    Type:    {f.FailureType}");
                if (f.FailureMessage != null) sb.AppendLine($"    Message: {f.FailureMessage}");
                if (!string.IsNullOrWhiteSpace(f.StackTrace))
                {
                    sb.AppendLine("    Stack trace:");
                    foreach (var line in f.StackTrace.Split('\n'))
                        if (!string.IsNullOrWhiteSpace(line))
                            sb.AppendLine($"      {line.TrimEnd()}");
                }
                sb.AppendLine();
                i++;
            }
        }
        else
        {
            sb.AppendLine("No failures — all tests passed!");
        }

        sb.AppendLine();
        sb.AppendLine("========================================");
        sb.AppendLine("  RAW CONSOLE OUTPUT");
        sb.AppendLine("========================================");
        sb.AppendLine(rawLog);

        File.WriteAllText(path, sb.ToString());
        return path;
    }
}