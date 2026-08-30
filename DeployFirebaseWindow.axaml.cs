using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AndroidCommandCenter.Models;
using AndroidCommandCenter.UI;

namespace AndroidCommandCenter;

public partial class DeployFirebaseWindow : Window
{
    private readonly FieldValidator _v;
    private readonly bool _ready;

    public DeployFirebaseWindow() : this(new DeployDraft("", "", "", "")) { }

    public DeployFirebaseWindow(DeployDraft draft)
    {
        InitializeComponent();
        ApkBox.Text = draft.ApkPath;
        GroupsBox.Text = draft.TesterGroups;
        EmailsBox.Text = draft.TesterEmails;
        NotesBox.Text = draft.ReleaseNotes;

        _v = new FieldValidator(DeployBtn, SummaryText)
            .Rule(ApkBox, ApkErr, ValidateApk)
            // At least one of groups/emails, surfaced on the groups field
            .Rule(GroupsBox, GroupsErr, _ =>
                string.IsNullOrWhiteSpace(GroupsBox.Text) && string.IsNullOrWhiteSpace(EmailsBox.Text)
                    ? "Enter at least one tester group or email." : null)
            .Rule(EmailsBox, EmailsErr, t =>
                string.IsNullOrWhiteSpace(t) ? null : FieldValidator.Email(t, "Tester emails"));

        _ready = true;
        _v.Run();
    }

    private string? ValidateApk(string v)
    {
        ApkInfo.Text = "";
        v = v.Trim();
        if (v.Length == 0) return "Select the APK to upload.";
        if (!File.Exists(v)) return $"File not found: {v}";
        if (!v.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) return "File must be an .apk.";
        if (Path.GetFileName(v).Contains("unsigned", StringComparison.OrdinalIgnoreCase))
            return "This APK is unsigned — Firebase will reject it. Use Build Signed APK first.";

        var fi = new FileInfo(v);
        if (fi.Length < 100_000) return "APK is suspiciously small (<100 KB) — probably a failed build.";
        ApkInfo.Text = $"{fi.Length / 1_048_576.0:F1} MB · built {fi.LastWriteTime:g}";
        return null;
    }

    private void Field_Changed(object? s, TextChangedEventArgs e) { if (_ready) _v.Run(); }

    private async void BrowseApk_Click(object? s, RoutedEventArgs e)
    {
        var path = await Dialogs.PickFileAsync(this, "Select APK", ApkBox.Text, Dialogs.ApkFilters);
        if (path != null) ApkBox.Text = path;
    }

    private void Deploy_Click(object? s, RoutedEventArgs e)
    {
        if (!_v.Run()) return;
        Close(new DeployDraft(ApkBox.Text!.Trim(), GroupsBox.Text ?? "",
                              EmailsBox.Text ?? "", NotesBox.Text ?? ""));
    }

    private void Cancel_Click(object? s, RoutedEventArgs e) => Close(null);
}
