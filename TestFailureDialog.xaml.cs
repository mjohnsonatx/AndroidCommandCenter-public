using System.Windows;

namespace AndroidCommandCenter;

public partial class TestFailureDialog : Window
{
    public TestFailureDialog(string title, string details, bool requireConfirm)
    {
        InitializeComponent();
        TitleText.Text = title;
        DetailsBox.Text = details;

        if (requireConfirm)
        {
            ConfirmCheck.Visibility = Visibility.Visible;
            ContinueBtn.IsEnabled = false;
        }
    }

    private void Confirm_Changed(object s, RoutedEventArgs e) =>
        ContinueBtn.IsEnabled = ConfirmCheck.IsChecked == true;

    private void Continue_Click(object s, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Stop_Click(object s, RoutedEventArgs e) { DialogResult = false; Close(); }
}