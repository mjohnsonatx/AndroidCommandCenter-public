using System.IO;
using System.Text;

namespace AndroidCommandCenter.Services;

/// <summary>
/// Pipeline log that also maintains a rolling window of recent output so a
/// failing stage can record what happened immediately before it, plus the lines
/// that arrive just after. Without this, a non-zero exit code in the manifest is
/// undiagnosable once the full log has been rotated away.
/// </summary>
public sealed class CiLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Queue<string> _recent = new();
    private readonly int _contextSize;
    private readonly object _gate = new();

    private List<string>? _captureAfter;
    private int _captureAfterRemaining;

    public string FilePath { get; }
    public event Action<string>? LineWritten;

    public CiLogger(string runId, string projectName, int contextSize = 50)
    {
        _contextSize = contextSize;
        FilePath = Path.Combine(Paths.PipelineLogs,
            $"CI_{Sanitize(projectName)}_{runId}.log");
        _writer = new StreamWriter(
            new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read),
            Encoding.UTF8) { AutoFlush = true };   // survive a hard kill

        Header($"CI RUN {runId}", $"Project: {projectName}",
               $"Host: {Environment.MachineName}",
               $"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
    }

    public void Line(string text)
    {
        lock (_gate)
        {
            _writer.WriteLine(text);

            _recent.Enqueue(text);
            while (_recent.Count > _contextSize) _recent.Dequeue();

            if (_captureAfterRemaining > 0 && _captureAfter != null)
            {
                _captureAfter.Add(text);
                _captureAfterRemaining--;
            }
        }
        LineWritten?.Invoke(text);
    }

    public void Section(string title)
    {
        Line("");
        Line("########################################################");
        Line($"#  STAGE: {title}");
        Line($"#  {DateTime.Now:HH:mm:ss}");
        Line("########################################################");
    }

    public void Header(params string[] lines)
    {
        Line("========================================================");
        foreach (var l in lines) Line("  " + l);
        Line("========================================================");
    }

    /// <summary>Snapshot of the lines leading up to now.</summary>
    public List<string> ContextBefore()
    {
        lock (_gate) return _recent.ToList();
    }

    /// <summary>
    /// Begin collecting the next N lines into the returned list, so a stage can
    /// record the teardown output that follows its failure (stack traces and
    /// Gradle's "what went wrong" block usually arrive after the exit).
    /// </summary>
    public List<string> BeginCaptureAfter(int lines = 30)
    {
        lock (_gate)
        {
            _captureAfter = new List<string>();
            _captureAfterRemaining = lines;
            return _captureAfter;
        }
    }

    public void EndCaptureAfter()
    {
        lock (_gate) { _captureAfterRemaining = 0; _captureAfter = null; }
    }

    public void Dispose()
    {
        try
        {
            Line("");
            Line($"Finished: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            _writer.Dispose();
        }
        catch { }
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
}