namespace AndroidCommandCenter.Services;

/// <summary>
/// Bounded display history, independent of the UI and the complete pipeline log.
/// Producers keep collecting output while the panel is hidden or paused.
/// </summary>
public sealed class ActivityLogBuffer
{
    public const int MaxCharacters = 400_000;
    private const int MaxLineCharacters = 8_000;
    private const int MaxLines = 10_000;
    private const string TrimMarker = "… older output trimmed from this view …";
    private readonly object _gate = new();
    private readonly Queue<string> _entries = new();
    private int _characters;
    private long _revision;
    private long _lines;
    private bool _trimmed;
    private string _latest = "No output yet";

    public void Append(string message)
    {
        lock (_gate)
        {
            _lines += 1 + message.Count(c => c == '\n');
            _revision++;
            var lastLine = message.AsSpan().TrimEnd();
            lastLine = lastLine[(lastLine.LastIndexOf('\n') + 1)..].Trim();
            if (!lastLine.IsEmpty)
                _latest = lastLine[..Math.Min(180, lastLine.Length)].ToString();

            foreach (string raw in message.Split('\n'))
            {
                string entry = raw.TrimEnd('\r');
                if (entry.Length > MaxLineCharacters)
                    entry = "… " + entry[^MaxLineCharacters..];
                _entries.Enqueue(entry);
                _characters += entry.Length + Environment.NewLine.Length;
            }
            int capacity = MaxCharacters - TrimMarker.Length - Environment.NewLine.Length;
            while ((_characters > capacity || _entries.Count > MaxLines) && _entries.TryDequeue(out var oldest))
            {
                _characters -= oldest.Length + Environment.NewLine.Length;
                _trimmed = true;
            }
        }
    }

    public ActivityLogSnapshot Read(long renderedRevision, bool includeEntries)
    {
        lock (_gate)
        {
            string[]? entries = null;
            if (includeEntries && renderedRevision != _revision)
            {
                entries = _trimmed ? [TrimMarker, .. _entries] : _entries.ToArray();
            }
            return new(_revision, _lines, _latest, entries);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _characters = 0;
            _lines = 0;
            _trimmed = false;
            _latest = "Log cleared";
            _revision++;
        }
    }
}

public sealed record ActivityLogSnapshot(long Revision, long Lines, string Latest, string[]? Entries);
