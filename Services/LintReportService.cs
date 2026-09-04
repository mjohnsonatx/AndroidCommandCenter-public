using System.IO;
using System.Text;
using System.Xml.Linq;

namespace AndroidCommandCenter.Services;

public sealed record LintIssue(string Severity, string Id, string Message, string Location);

public sealed class LintReport
{
    public List<LintIssue> Issues { get; } = new();
    public string? XmlPath { get; set; }
    public string? HtmlPath { get; set; }

    public int Errors => Issues.Count(i =>
        i.Severity is "Error" or "Fatal");
    public int Warnings => Issues.Count(i => i.Severity == "Warning");
    public int Informational => Issues.Count(i => i.Severity == "Information");

    public string Summary =>
        XmlPath == null
            ? "No lint XML report found — the lint task may not have run."
            : $"Lint: {Errors} error(s), {Warnings} warning(s), {Informational} info. ({XmlPath})";

    public string Detail
    {
        get
        {
            var sb = new StringBuilder();
            // Errors first — that's what blocks a release.
            foreach (var i in Issues
                         .OrderBy(i => i.Severity switch
                         { "Fatal" => 0, "Error" => 1, "Warning" => 2, _ => 3 })
                         .Take(80))
                sb.AppendLine($"[{i.Severity}] {i.Id}: {i.Message}\n    {i.Location}");
            if (Issues.Count > 80) sb.AppendLine($"…and {Issues.Count - 80} more.");
            return sb.ToString();
        }
    }
}

public static class LintReportService
{
    /// <summary>
    /// Finds and parses the Android Lint XML report for a variant. AGP writes to
    /// app/build/reports/lint-results-{variant}.xml (newer) or lint-results.xml
    /// (older / no-variant task); both are checked, newest wins.
    /// </summary>
    public static LintReport Parse(string projectPath, string variant)
    {
        var report = new LintReport();
        var candidates = new List<string>();

        foreach (var module in Modules(projectPath))
        {
            var reports = Path.Combine(module, "build", "reports");
            if (!Directory.Exists(reports)) continue;
            candidates.AddRange(Directory.GetFiles(reports, "lint-results*.xml", SearchOption.TopDirectoryOnly));
        }

        // Never accept a report from another variant. A stale Debug or Staging
        // report must not make a failed productionRelease lint invocation look
        // successful.
        string? xml = candidates
            .Where(f => Path.GetFileName(f)
                .Contains($"-{variant}.", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();

        if (xml == null) return report;
        report.XmlPath = xml;
        var html = Path.ChangeExtension(xml, ".html");
        if (File.Exists(html)) report.HtmlPath = html;

        try
        {
            var doc = XDocument.Load(xml);
            foreach (var issue in doc.Descendants("issue"))
            {
                var loc = issue.Element("location");
                string where = loc == null ? "" :
                    $"{loc.Attribute("file")?.Value}" +
                    (loc.Attribute("line") is { } l ? $":{l.Value}" : "");
                report.Issues.Add(new LintIssue(
                    issue.Attribute("severity")?.Value ?? "Unknown",
                    issue.Attribute("id")?.Value ?? "?",
                    issue.Attribute("message")?.Value ?? "",
                    where));
            }
        }
        catch { /* report stays empty; Summary explains */ }

        return report;
    }

    private static IEnumerable<string> Modules(string projectPath)
    {
        yield return Path.Combine(projectPath, "app");
        yield return projectPath;
        if (!Directory.Exists(projectPath)) yield break;
        foreach (var d in Directory.GetDirectories(projectPath))
            if (!Path.GetFileName(d).StartsWith('.')) yield return d;
    }
}
