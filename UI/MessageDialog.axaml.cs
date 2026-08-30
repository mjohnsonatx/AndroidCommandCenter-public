using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace AndroidCommandCenter.UI;

public partial class MessageDialog : Window
{
    public MessageDialog() => InitializeComponent();

    public sealed record Options(
        string Title,
        string? Message = null,
        IEnumerable<string>? Bullets = null,
        string? Details = null,
        ToastSeverity Severity = ToastSeverity.Info,
        string PrimaryLabel = "OK",
        string? SecondaryLabel = null,
        bool PrimaryIsDanger = false,
        string? ConfirmCheckboxText = null);

    /// <summary>Returns true if the primary button was clicked.</summary>
    public static async Task<bool> ShowAsync(Window owner, Options o)
    {
        var d = new MessageDialog();
        d.TitleText.Text = o.Title;
        d.Title = o.Title;
        d.MessageText.Text = o.Message ?? "";
        d.MessageText.IsVisible = !string.IsNullOrWhiteSpace(o.Message);

        var (icon, color) = o.Severity switch
        {
            ToastSeverity.Success => ("✔", "#a6e3a1"),
            ToastSeverity.Warning => ("⚠", "#f9e2af"),
            ToastSeverity.Error   => ("✖", "#f38ba8"),
            _                     => ("ℹ", "#89b4fa"),
        };
        d.IconText.Text = icon;
        d.IconText.Foreground = new SolidColorBrush(Color.Parse(color));

        var bullets = o.Bullets?.ToList();
        if (bullets is { Count: > 0 })
        {
            d.BulletList.ItemsSource = bullets;
            d.BulletList.IsVisible = true;
        }
        if (!string.IsNullOrWhiteSpace(o.Details))
        {
            d.DetailsBox.Text = o.Details;
            d.DetailsExpander.IsVisible = true;
        }

        d.PrimaryBtn.Content = o.PrimaryLabel;
        d.PrimaryBtn.Classes.Add(o.PrimaryIsDanger ? "danger" : "primary");
        d.SecondaryBtn.Content = o.SecondaryLabel;
        d.SecondaryBtn.IsVisible = o.SecondaryLabel != null;

        if (o.ConfirmCheckboxText != null)
        {
            d.ConfirmCheck.Content = o.ConfirmCheckboxText;
            d.ConfirmCheck.IsVisible = true;
            d.PrimaryBtn.IsEnabled = false;
        }

        return await d.ShowDialog<bool?>(owner) == true;
    }

    private void Confirm_Changed(object? s, RoutedEventArgs e) =>
        PrimaryBtn.IsEnabled = ConfirmCheck.IsChecked == true;
    private void Primary_Click(object? s, RoutedEventArgs e) => Close(true);
    private void Secondary_Click(object? s, RoutedEventArgs e) => Close(false);
}
