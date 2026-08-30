using System.Text.RegularExpressions;

namespace AndroidCommandCenter.Ci;

/// <summary>
/// Captures every line emitted during a stage and extracts a failure window.
/// </summary>
public sealed class StageOutput
{
    public const int ContextLines = 50;

    private readonly List<string> _lines = new();
    private readonly object _gate = new();

    // First-error heuristics across gradle, git, adb, lint, and our own markers.
    private static readonly Regex ErrorMarker = new(
        @"(^|\s)(FAILED|FAILURE:|BUILD FAILED|error:|Error:|ERROR|\[ERROR\]|fatal:|CONFLICT|" +
        @"Exception|Caused by:|Execution failed|What went wrong|Tests? failed|>>> FAILED|\[X\])",
        RegexOptions.Compiled);

    public void Add(string line)
    {
        lock (_gate) _lines.Add(line);
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate) return _lines.ToList();
    }

    public int Count { get { lock (_gate) return _lines.Count; } }

    /// <summary>
    /// Finds the first line that looks like an error and returns it with
    /// ContextLines of surrounding output. Falls back to the tail when no
    /// marker is found (e.g. a silent non-zero exit).
    /// </summary>
    public FailureContext Extract(string message, Exception? ex = null)
    {
        var lines = Snapshot();
        var ctx = new FailureContext { Message = message, Exception = ex?.ToString() };
        if (lines.Count == 0) return ctx;

        int idx = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (ErrorMarker.IsMatch(lines[i])) { idx = i; break; }
        }

        if (idx < 0)
        {
            ctx.Before = lines.Skip(Math.Max(0, lines.Count - ContextLines * 2)).ToList();
            return ctx;
        }

        ctx.ErrorLineIndex = idx;
        ctx.ErrorLine = lines[idx];
        ctx.Before = lines.Skip(Math.Max(0, idx - ContextLines)).Take(Math.Min(ContextLines, idx)).ToList();
        ctx.After = lines.Skip(idx + 1).Take(ContextLines).ToList();
        return ctx;
    }
}
