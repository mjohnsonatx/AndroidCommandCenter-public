using System.IO;
using System.Text;

namespace AndroidCommandCenter.Services;

public class PipelineLogger
{
    private readonly StringBuilder _sb = new();
    private readonly object _gate = new();
    public string FilePath { get; }

    public static string LogsDirectory
    {
        get
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PipelineLogs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public PipelineLogger(string prefix, string projectName, string? directory = null)
    {
        string dir = directory ?? Paths.PipelineLogs;
        string ts = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        FilePath = Path.Combine(dir, $"{prefix}_{projectName.Replace(" ", "")}_{ts}.txt");

        _sb.AppendLine("========================================");
        _sb.AppendLine($"  {prefix} PIPELINE LOG");
        _sb.AppendLine($"  Project: {projectName}");
        _sb.AppendLine($"  Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _sb.AppendLine("========================================");
    }

    public void Section(string title)
    {
        lock (_gate)
        {
            _sb.AppendLine();
            _sb.AppendLine("########################################");
            _sb.AppendLine($"#  STAGE: {title}");
            _sb.AppendLine("########################################");
        }
    }

    public void Line(string text)
    {
        lock (_gate) _sb.AppendLine(text);
    }

    public void Save()
    {
        string contents;
        lock (_gate)
        {
            _sb.AppendLine();
            _sb.AppendLine($"Finished: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            contents = _sb.ToString();
        }
        try { File.WriteAllText(FilePath, contents); } catch { }
    }
}
