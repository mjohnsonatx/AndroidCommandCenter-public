using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;

namespace AndroidCommandCenter.UI;

public enum ToastSeverity { Info, Success, Warning, Error }

public sealed class ToastItem : INotifyPropertyChanged
{
    public ToastSeverity Severity { get; init; }
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    /// <summary>Optional expandable detail (stack trace, failing tests, stderr).</summary>
    public string? Details { get; init; }
    /// <summary>Optional action, e.g. "Open log".</summary>
    public string? ActionLabel { get; init; }
    public Action? Action { get; init; }
    public DateTime Created { get; } = DateTime.Now;

    private bool _expanded;
    public bool IsExpanded
    {
        get => _expanded;
        set { if (_expanded != value) { _expanded = value; OnChanged(); OnChanged(nameof(ToggleLabel)); } }
    }

    public bool HasDetails => !string.IsNullOrWhiteSpace(Details);
    public bool HasAction => Action != null && !string.IsNullOrWhiteSpace(ActionLabel);
    public string ToggleLabel => IsExpanded ? "Hide details" : "Show details";
    public string Time => Created.ToString("HH:mm:ss");

    public IBrush Accent => Severity switch
    {
        ToastSeverity.Success => new SolidColorBrush(Color.Parse("#a6e3a1")),
        ToastSeverity.Warning => new SolidColorBrush(Color.Parse("#f9e2af")),
        ToastSeverity.Error   => new SolidColorBrush(Color.Parse("#f38ba8")),
        _                     => new SolidColorBrush(Color.Parse("#89b4fa")),
    };

    public string Icon => Severity switch
    {
        ToastSeverity.Success => "✔",
        ToastSeverity.Warning => "⚠",
        ToastSeverity.Error   => "✖",
        _                     => "ℹ",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// Thread-safe toast queue. Errors and warnings stay until dismissed;
/// info/success auto-dismiss. Also mirrors every toast to the log sink so the
/// pipeline log is a complete record even if the user never saw the toast.
/// </summary>
public sealed class Toaster
{
    public ObservableCollection<ToastItem> Items { get; } = new();
    public Action<string>? LogSink { get; set; }
    public int MaxVisible { get; set; } = 5;

    private static readonly TimeSpan InfoLife    = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan SuccessLife = TimeSpan.FromSeconds(6);

    public void Info(string title, string? message = null, string? details = null)
        => Show(ToastSeverity.Info, title, message, details);
    public void Success(string title, string? message = null, string? details = null,
                        string? actionLabel = null, Action? action = null)
        => Show(ToastSeverity.Success, title, message, details, actionLabel, action);
    public void Warning(string title, string? message = null, string? details = null,
                        string? actionLabel = null, Action? action = null)
        => Show(ToastSeverity.Warning, title, message, details, actionLabel, action);
    public void Error(string title, string? message = null, string? details = null,
                      string? actionLabel = null, Action? action = null)
        => Show(ToastSeverity.Error, title, message, details, actionLabel, action);

    public void Error(string title, Exception ex, string? actionLabel = null, Action? action = null)
        => Show(ToastSeverity.Error, title, Friendly(ex), ex.ToString(), actionLabel, action);

    public void Show(ToastSeverity sev, string title, string? message = null, string? details = null,
                     string? actionLabel = null, Action? action = null)
    {
        var item = new ToastItem
        {
            Severity = sev, Title = title, Message = message ?? "",
            Details = details, ActionLabel = actionLabel, Action = action
        };

        LogSink?.Invoke($"[{sev.ToString().ToUpperInvariant()}] {title}" +
                        (string.IsNullOrEmpty(message) ? "" : $" — {message}"));

        Dispatcher.UIThread.Post(() =>
        {
            while (Items.Count >= MaxVisible) Items.RemoveAt(0);
            Items.Add(item);

            var life = sev switch
            {
                ToastSeverity.Info    => InfoLife,
                ToastSeverity.Success => SuccessLife,
                _                     => TimeSpan.Zero   // sticky
            };
            if (life > TimeSpan.Zero)
                DispatcherTimer.RunOnce(() => Items.Remove(item), life);
        });
    }

    public void Dismiss(ToastItem item) => Dispatcher.UIThread.Post(() => Items.Remove(item));
    public void Clear() => Dispatcher.UIThread.Post(Items.Clear);

    /// <summary>Turn common exceptions into a sentence a person can act on.</summary>
    public static string Friendly(Exception ex) => ex switch
    {
        OperationCanceledException            => "The operation was cancelled.",
        System.ComponentModel.Win32Exception w when w.NativeErrorCode == 2
                                              => "Executable not found. Check the SDK/tool path.",
        System.ComponentModel.Win32Exception w when w.NativeErrorCode == 13
                                              => "Permission denied running the tool. Is it executable (chmod +x)?",
        System.IO.FileNotFoundException f     => $"File not found: {f.FileName ?? f.Message}",
        System.IO.DirectoryNotFoundException  => "Directory not found.",
        UnauthorizedAccessException           => "Permission denied.",
        System.Net.Http.HttpRequestException h => $"Network error: {h.Message}",
        TimeoutException                      => "The operation timed out.",
        _                                     => ex.Message
    };
}