using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AndroidCommandCenter.UI;

public partial class ToastHost : UserControl
{
    public ToastHost() => InitializeComponent();

    public Toaster? Toaster
    {
        get => DataContext as Toaster;
        set => DataContext = value;
    }

    private void Close_Click(object? s, RoutedEventArgs e)
    {
        if ((s as Button)?.Tag is ToastItem t) Toaster?.Dismiss(t);
    }

    private void Toggle_Click(object? s, RoutedEventArgs e)
    {
        if ((s as Button)?.Tag is ToastItem t) t.IsExpanded = !t.IsExpanded;
    }

    private void Action_Click(object? s, RoutedEventArgs e)
    {
        if ((s as Button)?.Tag is not ToastItem t) return;
        try { t.Action?.Invoke(); }
        catch (Exception ex) { Toaster?.Error("Action failed", ex); }
    }
}