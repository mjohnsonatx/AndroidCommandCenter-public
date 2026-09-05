using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AndroidCommandCenter.Services;

namespace AndroidCommandCenter.UI;

public enum ActivityLogLayout { Compact, Docked, Focused }

public partial class ActivityLogPanel : UserControl
{
    private readonly ActivityLogBuffer _buffer = new();
    private readonly DispatcherTimer _timer;
    private long _renderedRevision = -1;
    private long _renderedLines;
    private string[] _displayedEntries = [];
    private bool _following = true;
    private bool _keepOpenAfterRun;
    private bool _running;
    private string _operationStatus = "";

    public ActivityLogLayout Layout { get; private set; } = ActivityLogLayout.Compact;
    public event EventHandler? LayoutRequested;

    public ActivityLogPanel()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _timer.Tick += (_, _) => RefreshOutput();

        // Reading or selecting earlier output must not be interrupted by a new
        // batch replacing the rows or forcing its scroll position to the end.
        OutputList.AddHandler(PointerPressedEvent, (_, _) => PauseFollowing(), RoutingStrategies.Tunnel);
        OutputList.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (e.Delta.Y > 0) PauseFollowing();
        }, RoutingStrategies.Tunnel);
        OutputList.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key is Key.Up or Key.PageUp or Key.Home ||
                (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0)
                PauseFollowing();
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                await CopyOutput(selectedOnly: true);
            }
        }, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
        RefreshOutput();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    // Safe from process output threads; no control property is accessed here.
    public void Append(string message) => _buffer.Append(message);

    public void BeginOperation(string status)
    {
        _running = true;
        _operationStatus = status;
        if (Layout == ActivityLogLayout.Compact) SetLayout(ActivityLogLayout.Docked);
        RefreshOutput();
    }

    public void SetOperationStatus(string status)
    {
        _operationStatus = status;
        RefreshOutput();
    }

    public void EndOperation()
    {
        _running = false;
        if (!_keepOpenAfterRun) SetLayout(ActivityLogLayout.Compact);
        RefreshOutput();
    }

    public void KeepExpanded() => _keepOpenAfterRun = true;

    public void Clear()
    {
        _buffer.Clear();
        _renderedRevision = -1;
        _renderedLines = 0;
        _displayedEntries = [];
        OutputList.ItemsSource = _displayedEntries;
        RefreshOutput();
    }

    private void SetLayout(ActivityLogLayout layout)
    {
        Layout = layout;
        bool expanded = layout != ActivityLogLayout.Compact;
        OutputList.IsVisible = expanded;
        FollowButton.IsVisible = expanded;
        FocusButton.IsVisible = expanded;
        FocusButton.Content = layout == ActivityLogLayout.Focused ? "Restore" : "Expand";
        ToggleButton.Content = expanded ? "Hide" : "Show log";
        LayoutRequested?.Invoke(this, EventArgs.Empty);
        RefreshOutput();
    }

    private void RefreshOutput()
    {
        // Background output still goes into the bounded history when minimized.
        if (TopLevel.GetTopLevel(this) is Window { WindowState: WindowState.Minimized }) return;

        var snapshot = _buffer.Read(_renderedRevision, _following && Layout != ActivityLogLayout.Compact);
        ActivitySummary.Text = _running ? _operationStatus : snapshot.Latest;
        ActivityDot.Background = _running ? Brushes.LightGreen : Brushes.SlateGray;
        long unseen = Math.Max(0, snapshot.Lines - _renderedLines);
        LineCount.Text = !_following && unseen > 0
            ? $"{unseen:N0} new · paused" : $"{snapshot.Lines:N0} lines";
        FollowButton.Content = _following ? "Pause" : "Resume";
        if (snapshot.Entries is null) return;

        // Virtualization lays out only visible rows, even after a very noisy
        // build. Reformatting a full TextBox document on resize stalled the UI.
        _displayedEntries = snapshot.Entries;
        OutputList.ItemsSource = _displayedEntries;
        OutputList.SelectedItems?.Clear();
        if (_displayedEntries.Length > 0) OutputList.ScrollIntoView(_displayedEntries.Length - 1);
        _renderedRevision = snapshot.Revision;
        _renderedLines = snapshot.Lines;
    }

    private void PauseFollowing()
    {
        if (!_following) return;
        _following = false;
        _keepOpenAfterRun = true;
        RefreshOutput();
    }

    private void Follow_Click(object? sender, RoutedEventArgs e)
    {
        if (_following) PauseFollowing();
        else
        {
            _following = true;
            _renderedRevision = -1;
            RefreshOutput();
        }
    }

    private void Toggle_Click(object? sender, RoutedEventArgs e)
    {
        _keepOpenAfterRun = Layout == ActivityLogLayout.Compact;
        SetLayout(_keepOpenAfterRun ? ActivityLogLayout.Docked : ActivityLogLayout.Compact);
    }

    private void Focus_Click(object? sender, RoutedEventArgs e)
    {
        _keepOpenAfterRun = true;
        SetLayout(Layout == ActivityLogLayout.Focused ? ActivityLogLayout.Docked : ActivityLogLayout.Focused);
    }

    private void Clear_Click(object? sender, RoutedEventArgs e) => Clear();

    private async Task CopyOutput(bool selectedOnly)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var entries = selectedOnly
            ? OutputList.Selection.SelectedIndexes.Order().Select(index => _displayedEntries[index])
            : _displayedEntries.AsEnumerable();
        await clipboard.SetTextAsync(string.Join(Environment.NewLine, entries));
    }

    private async void CopySelected_Click(object? sender, RoutedEventArgs e) => await CopyOutput(selectedOnly: true);
    private async void CopyAll_Click(object? sender, RoutedEventArgs e) => await CopyOutput(selectedOnly: false);
}
