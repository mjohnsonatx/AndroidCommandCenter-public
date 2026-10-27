using System.Text.RegularExpressions;

namespace AndroidCommandCenter.Ci;

/// <summary>
/// Captures stage output and preserves tool diagnostics independently of the context window.
/// </summary>
public sealed class StageOutput
{
    public const int ContextLines = 50;
    private const int ExceptionLines = 200;

    private readonly List<string> _lines = new();
    private readonly object _gate = new();

    // First-error heuristics across gradle, git, adb, lint, and our own markers.
    private static readonly Regex ErrorMarker = new(
        @"(^|\s)(FAILED|FAILURE:|BUILD FAILED|error:|Error:|ERROR|\[ERROR\]|fatal:|CONFLICT|" +
        @"Exception|Caused by:|Execution failed|What went wrong|Tests? failed|>>> FAILED|\[X\])",
        RegexOptions.Compiled);
    private static readonly Regex FailedTask = new(
        @"^> Task (?<task>:\S+) FAILED\b|^Execution failed for task '(?<task>[^']+)'",
        RegexOptions.Compiled);

    private enum Section { None, Location, Problem, Exception }

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
    /// Gradle can print hundreds of asset/task lines between FAILED and its final
    /// explanation. Scan the whole stage for report sections and causal chains;
    /// center the context window on the explanation, not the earlier task marker.
    /// Preserve the old marker/tail fallback for Git, compiler and silent exits.
    /// </summary>
    public FailureContext Extract(string message, Exception? ex = null)
    {
        var lines = Snapshot();
        var ctx = new FailureContext { Message = message, Exception = ex?.ToString() };
        if (lines.Count == 0) return ctx;

        int firstError = -1, firstProblem = -1, firstCause = -1;
        Section section = Section.None;
        bool sectionOnStderr = false;
        var exception = new List<string>();
        int exceptionLineCount = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            string raw = lines[i];
            bool stderr = raw.StartsWith("[ERR]", StringComparison.Ordinal);
            string line = (stderr ? raw[5..] : raw).Trim();
            if (firstError < 0 && ErrorMarker.IsMatch(line)) firstError = i;

            var task = FailedTask.Match(line);
            if (task.Success && !ctx.FailedTasks.Contains(task.Groups["task"].Value))
                ctx.FailedTasks.Add(task.Groups["task"].Value);
            if (line.StartsWith("Caused by:", StringComparison.Ordinal))
            {
                if (firstCause < 0) firstCause = i;
                ctx.Causes.Add(line);
            }

            // ProcessRunner merges stdout/stderr asynchronously. Do not let asset
            // chatter on stdout enter or close a Gradle report section on stderr.
            if (section != Section.None && stderr != sectionOnStderr) continue;
            if (line.StartsWith("* ", StringComparison.Ordinal))
            {
                section = line switch
                {
                    "* Where:" => Section.Location,
                    "* What went wrong:" => Section.Problem,
                    "* Exception is:" => Section.Exception,
                    _ => Section.None
                };
                sectionOnStderr = stderr;
                if (section == Section.Problem && firstProblem < 0) firstProblem = i;
                continue;
            }
            if (line.StartsWith("BUILD FAILED", StringComparison.Ordinal) ||
                line.StartsWith("BUILD SUCCESSFUL", StringComparison.Ordinal) ||
                line.StartsWith("====", StringComparison.Ordinal) ||
                line.StartsWith("FAILURE:", StringComparison.Ordinal))
                section = Section.None;

            if (line.Length == 0) continue;
            switch (section)
            {
                case Section.Location:
                    ctx.Locations.Add(line);
                    break;
                case Section.Problem:
                    ctx.Problems.Add(line);
                    break;
                case Section.Exception:
                    exceptionLineCount++;
                    if (exception.Count < ExceptionLines) exception.Add(stderr ? raw[5..].TrimStart(' ') : raw);
                    break;
            }
        }
        if (exceptionLineCount > ExceptionLines)
            exception.Add($"... {exceptionLineCount - ExceptionLines} exception lines omitted; see full log. All Caused by lines are retained separately.");
        if (exception.Count > 0) ctx.ToolException = string.Join(Environment.NewLine, exception);

        int idx = firstProblem >= 0 ? firstProblem : firstCause >= 0 ? firstCause : firstError;
        // Associate the failure with the last echoed command before its report.
        ctx.Command = lines.Take(idx >= 0 ? idx + 1 : lines.Count)
            .LastOrDefault(l => l.StartsWith("$ ", StringComparison.Ordinal));
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
