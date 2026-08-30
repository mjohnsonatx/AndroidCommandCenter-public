using System.Globalization;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AndroidCommandCenter.Models;
using AndroidCommandCenter.UI;

namespace AndroidCommandCenter;

public enum CiPipelineDialogAction { Save, DryRun, Promote }

public sealed record CiPipelineDialogResult(
    CiPipelineConfig Config, CiPipelineDialogAction Action);

public partial class CiPipelineWindow : Window
{
    private readonly string _lastScheduledRunKey;

    public CiPipelineWindow() : this(new CiPipelineConfig()) { }

    public CiPipelineWindow(CiPipelineConfig config)
    {
        InitializeComponent();
        _lastScheduledRunKey = config.LastScheduledRunKey;
        RemoteBox.Text = config.Remote;
        DevBox.Text = config.DevBranch;
        StagingBox.Text = config.StagingBranch;
        MainBox.Text = config.MainBranch;
        VariantBox.Text = config.GateVariant;

        LintCheck.IsChecked = config.RunLint;
        LintWarningsCheck.IsChecked = config.LintFailsOnWarning;
        UnitCheck.IsChecked = config.RunUnitTests;
        InstrumentCheck.IsChecked = config.RunInstrumentTests;
        SecretCheck.IsChecked = config.RunSecretScan;
        ChecksumCheck.IsChecked = config.RunChecksumValidation;
        VersionCheck.IsChecked = config.RequireVersionCodeBump;

        PromoteCheck.IsChecked = config.PromoteToMain;
        PushCheck.IsChecked = config.PushMain;
        TagCheck.IsChecked = config.TagRelease;
        BackMergeCheck.IsChecked = config.BackMergeToDev;
        TagPrefixBox.Text = config.TagPrefix;

        ScheduleCheck.IsChecked = config.ScheduleEnabled;
        ScheduleTimesBox.Text = config.ScheduleTimes;
        FirestoreCheck.IsChecked = config.PublishToFirestore;
        FirestoreProjectBox.Text = config.FirestoreProjectId;
        FirestoreCredentialsBox.Text = config.FirestoreCredentialsJson;
        FirestoreCollectionBox.Text = config.FirestoreCollection;
        UpdateVisibility();
    }

    private void Schedule_Changed(object? sender, RoutedEventArgs e) => UpdateVisibility();
    private void Firestore_Changed(object? sender, RoutedEventArgs e) => UpdateVisibility();

    private void UpdateVisibility()
    {
        SchedulePanel.IsVisible = ScheduleCheck.IsChecked == true;
        FirestorePanel.IsVisible = FirestoreCheck.IsChecked == true;
    }

    private async void BrowseCredentials_Click(object? sender, RoutedEventArgs e)
    {
        var path = await Dialogs.PickFileAsync(this, "Select Firestore service-account JSON",
            FirestoreCredentialsBox.Text, Dialogs.JsonFilters);
        if (path != null) FirestoreCredentialsBox.Text = path;
    }

    private CiPipelineConfig? Build()
    {
        string remote = RemoteBox.Text?.Trim() ?? "";
        string dev = DevBox.Text?.Trim() ?? "";
        string staging = StagingBox.Text?.Trim() ?? "";
        string main = MainBox.Text?.Trim() ?? "";
        string variant = VariantBox.Text?.Trim() ?? "";
        string prefix = TagPrefixBox.Text?.Trim() ?? "";
        var problems = new List<string>();

        if (!GitNames.IsValid(remote)) problems.Add("Remote is not a valid Git name.");
        if (!GitNames.IsValid(dev)) problems.Add("Development branch is not a valid Git name.");
        if (!GitNames.IsValid(staging)) problems.Add("Staging branch is not a valid Git name.");
        if (!GitNames.IsValid(main)) problems.Add("Production branch is not a valid Git name.");
        if (new[] { dev, staging, main }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            problems.Add("Development, staging, and production must be three different branches.");
        if (variant.Length == 0) problems.Add("Gate variant is required.");
        if (TagCheck.IsChecked == true && prefix.Length == 0)
            problems.Add("A tag prefix is required when release tagging is enabled.");
        if (PromoteCheck.IsChecked != true &&
            (PushCheck.IsChecked == true || TagCheck.IsChecked == true || BackMergeCheck.IsChecked == true))
            problems.Add("Push, tagging, and back-merge require promotion to be enabled.");
        if (ScheduleCheck.IsChecked == true && !ValidSchedule(ScheduleTimesBox.Text ?? ""))
            problems.Add("Schedule times must be 24-hour HH:mm values separated by commas.");

        string firestoreProject = FirestoreProjectBox.Text?.Trim() ?? "";
        string firestoreCredentials = FirestoreCredentialsBox.Text?.Trim() ?? "";
        string firestoreCollection = FirestoreCollectionBox.Text?.Trim() ?? "";
        if (FirestoreCheck.IsChecked == true)
        {
            if (firestoreProject.Length == 0) problems.Add("Firestore project ID is required.");
            if (firestoreCollection.Length == 0) problems.Add("Firestore collection is required.");
            if (!File.Exists(firestoreCredentials)) problems.Add("Firestore service-account JSON does not exist.");
        }

        if (problems.Count > 0)
        {
            ErrorText.Text = string.Join(" ", problems);
            ErrorText.IsVisible = true;
            return null;
        }

        ErrorText.IsVisible = false;
        return new CiPipelineConfig
        {
            Remote = remote, DevBranch = dev, StagingBranch = staging,
            MainBranch = main, GateVariant = variant,
            RunLint = LintCheck.IsChecked == true,
            LintFailsOnWarning = LintWarningsCheck.IsChecked == true,
            RunUnitTests = UnitCheck.IsChecked == true,
            RunInstrumentTests = InstrumentCheck.IsChecked == true,
            RunSecretScan = SecretCheck.IsChecked == true,
            RunChecksumValidation = ChecksumCheck.IsChecked == true,
            RequireVersionCodeBump = VersionCheck.IsChecked == true,
            PromoteToMain = PromoteCheck.IsChecked == true,
            PushMain = PushCheck.IsChecked == true,
            TagRelease = TagCheck.IsChecked == true,
            TagPrefix = prefix,
            BackMergeToDev = BackMergeCheck.IsChecked == true,
            ScheduleEnabled = ScheduleCheck.IsChecked == true,
            ScheduleTimes = ScheduleTimesBox.Text?.Trim() ?? "",
            LastScheduledRunKey = _lastScheduledRunKey,
            FirestoreProjectId = firestoreProject,
            FirestoreCredentialsJson = firestoreCredentials,
            FirestoreCollection = firestoreCollection,
            PublishToFirestore = FirestoreCheck.IsChecked == true
        };
    }

    private static bool ValidSchedule(string value)
    {
        var entries = value.Split([',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return entries.Length > 0 && entries.All(raw =>
            TimeSpan.TryParseExact(raw, [@"h\:mm", @"hh\:mm"],
                CultureInfo.InvariantCulture, out var t) &&
            t >= TimeSpan.Zero && t < TimeSpan.FromDays(1));
    }

    private void Finish(CiPipelineDialogAction action)
    {
        if (Build() is { } config) Close(new CiPipelineDialogResult(config, action));
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => Finish(CiPipelineDialogAction.Save);
    private void DryRun_Click(object? sender, RoutedEventArgs e) => Finish(CiPipelineDialogAction.DryRun);
    private void Run_Click(object? sender, RoutedEventArgs e) => Finish(CiPipelineDialogAction.Promote);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(null);
}
