using System.Windows;
using System.Windows.Controls;
using AndroidCommandCenter.Models;

namespace AndroidCommandCenter;

public partial class FirebaseConfigWindow : Window
{
    private readonly CiCdConfig _cfg;

    public FirebaseConfigWindow(CiCdConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        AppIdBox.Text = cfg.FirebaseAppId;
        JsonBox.Text = cfg.CredentialsJsonPath;
        CliBox.Text = cfg.FirebaseCliPath;
        Validate();
    }

    private void Field_Changed(object s, TextChangedEventArgs e) => Validate();

    private void Validate() =>
        SaveBtn.IsEnabled = !string.IsNullOrWhiteSpace(AppIdBox.Text)
                         && !string.IsNullOrWhiteSpace(JsonBox.Text);

    private void BrowseJson_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() == true) JsonBox.Text = dlg.FileName;
    }

    private void Save_Click(object s, RoutedEventArgs e)
    {
        _cfg.FirebaseAppId = AppIdBox.Text.Trim();
        _cfg.CredentialsJsonPath = JsonBox.Text.Trim();
        _cfg.FirebaseCliPath = string.IsNullOrWhiteSpace(CliBox.Text) ? "firebase" : CliBox.Text.Trim();
        DialogResult = true; Close();
    }

    private void Cancel_Click(object s, RoutedEventArgs e) { DialogResult = false; Close(); }
}