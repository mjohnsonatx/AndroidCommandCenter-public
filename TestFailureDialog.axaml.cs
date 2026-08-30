using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AndroidCommandCenter;

public partial class TestFailureDialog : Window
{
    private readonly string _details;

    public TestFailureDialog(string title, string details, bool requireConfirm, int failed = 0, int total = 0)
    {
        InitializeComponent();
        TitleText.Text = title;
        _details = details;
        DetailsBox.Text = details;
        CountText.Text = total > 0
            ? $"{failed} of {total} tests failed. The pipeline is paused awaiting your decision."
            : "The pipeline is paused awaiting your decision.";

        if (requireConfirm)
        {
            ConfirmCheck.IsVisible = true;
            ContinueBtn.IsEnabled = false;
        }
    }

    private void Confirm_Changed(object? s, RoutedEventArgs e) =>
        ContinueBtn.IsEnabled = ConfirmCheck.IsChecked == true;

    private async void Copy_Click(object? s, RoutedEventArgs e)
    {
        var clip = GetTopLevel(this)?.Clipboard;
        if (clip != null) await clip.SetTextAsync(_details);
    }

    private void Continue_Click(object? s, RoutedEventArgs e) => Close(true);
    private void Stop_Click(object? s, RoutedEventArgs e) => Close(false);

    /// <summary>Closing the window with the X means "stop" — the safe default.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
    }
}