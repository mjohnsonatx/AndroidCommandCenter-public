using System.Windows;
using System.IO;
using System.Windows.Controls;
using AndroidCommandCenter.Models;

namespace AndroidCommandCenter;

public partial class DeployFirebaseWindow : Window
{
    private readonly CiCdConfig _cfg;
    public string ApkPath => ApkBox.Text.Trim();

    public DeployFirebaseWindow(CiCdConfig cfg, string? initialApkPath)
    {
        InitializeComponent();
        _cfg = cfg;
        ApkBox.Text = initialApkPath ?? "";
        GroupsBox.Text = cfg.TesterGroups;
        EmailsBox.Text = cfg.TesterEmails;
        NotesBox.Text = cfg.ReleaseNotes;
        Validate();
    }

    private void Field_Changed(object s, TextChangedEventArgs e) => Validate();

    private void Validate() =>
        DeployBtn.IsEnabled = File.Exists(ApkBox.Text)
                           && (!string.IsNullOrWhiteSpace(GroupsBox.Text)
                               || !string.IsNullOrWhiteSpace(EmailsBox.Text));

    private void BrowseApk_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "APK (*.apk)|*.apk|All files|*.*"
        };

        var current = ApkBox.Text.Trim();
        var dir = Path.GetDirectoryName(current);
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            dlg.InitialDirectory = dir;
        if (File.Exists(current))
            dlg.FileName = Path.GetFileName(current);

        if (dlg.ShowDialog() == true)
        {
            ApkBox.Text = dlg.FileName;
            Validate();
        }
    }

    private void Deploy_Click(object s, RoutedEventArgs e)
    {
        _cfg.TesterGroups = GroupsBox.Text.Trim();
        _cfg.TesterEmails = EmailsBox.Text.Trim();
        _cfg.ReleaseNotes = NotesBox.Text;
        DialogResult = true; Close();
    }

    private void Cancel_Click(object s, RoutedEventArgs e) { DialogResult = false; Close(); }
}
