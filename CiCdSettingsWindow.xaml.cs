using System.Diagnostics;
using System.Windows;
using AndroidCommandCenter.Models;

namespace AndroidCommandCenter;

public partial class CiCdSettingsWindow : Window
{
    private readonly CiCdConfig _config;

    public CiCdSettingsWindow(CiCdConfig config)
    {
        InitializeComponent();
        _config = config;

        // Load current values
        KeystoreBox.Text = config.KeystorePath;
        StorePassBox.Password = config.StorePassword;
        KeyAliasBox.Text = config.KeyAlias;
        KeyPassBox.Password = config.KeyPassword;
        FirebaseCliBox.Text = config.FirebaseCliPath;
        AppIdBox.Text = config.FirebaseAppId;
        GroupsBox.Text = config.DistributionGroups;
        TokenBox.Password = config.FirebaseToken;
        ServiceAccountBox.Text = config.ServiceAccountPath;
        ReleaseNotesBox.Text = config.ReleaseNotes;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _config.KeystorePath = KeystoreBox.Text.Trim();
        _config.StorePassword = StorePassBox.Password;
        _config.KeyAlias = KeyAliasBox.Text.Trim();
        _config.KeyPassword = KeyPassBox.Password;
        _config.FirebaseCliPath = FirebaseCliBox.Text.Trim();
        _config.FirebaseAppId = AppIdBox.Text.Trim();
        _config.DistributionGroups = GroupsBox.Text.Trim();
        _config.FirebaseToken = TokenBox.Password;
        _config.ServiceAccountPath = ServiceAccountBox.Text.Trim();
        _config.ReleaseNotes = ReleaseNotesBox.Text;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BrowseKeystore_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Keystore (*.jks;*.keystore)|*.jks;*.keystore|All files|*.*",
            Title = "Select keystore file"
        };
        if (dlg.ShowDialog() == true) KeystoreBox.Text = dlg.FileName;
    }

    private void BrowseServiceAccount_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "JSON files (*.json)|*.json",
            Title = "Select service account JSON"
        };
        if (dlg.ShowDialog() == true) ServiceAccountBox.Text = dlg.FileName;
    }

    private async void TestFirebase_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {FirebaseCliBox.Text} --version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            MessageBox.Show(
                proc.ExitCode == 0
                    ? $"Firebase CLI found!\n{output.Trim()}"
                    : "Firebase CLI not found.\n\nInstall: npm install -g firebase-tools",
                "Firebase CLI Test",
                MessageBoxButton.OK,
                proc.ExitCode == 0 ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", "Firebase CLI Test",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}