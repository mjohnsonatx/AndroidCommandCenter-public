using System.Windows;
using System.Windows.Controls;
using AndroidCommandCenter.Models;

namespace AndroidCommandCenter;

public partial class DeployFirebaseWindow : Window
{
    private readonly CiCdConfig _cfg;

    public DeployFirebaseWindow(CiCdConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        GroupsBox.Text = cfg.TesterGroups;
        EmailsBox.Text = cfg.TesterEmails;
        NotesBox.Text = cfg.ReleaseNotes;
        Validate();
    }

    private void Field_Changed(object s, TextChangedEventArgs e) => Validate();

    private void Validate() =>
        DeployBtn.IsEnabled = !string.IsNullOrWhiteSpace(GroupsBox.Text)
                           || !string.IsNullOrWhiteSpace(EmailsBox.Text);

    private void Deploy_Click(object s, RoutedEventArgs e)
    {
        _cfg.TesterGroups = GroupsBox.Text.Trim();
        _cfg.TesterEmails = EmailsBox.Text.Trim();
        _cfg.ReleaseNotes = NotesBox.Text;
        DialogResult = true; Close();
    }

    private void Cancel_Click(object s, RoutedEventArgs e) { DialogResult = false; Close(); }
}