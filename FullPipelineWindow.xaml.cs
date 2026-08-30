using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AndroidCommandCenter.Models;
using AndroidCommandCenter.Services;
using AndroidCommandCenter.UI;

namespace AndroidCommandCenter;

public partial class FullPipelineWindow : Window
{
    private readonly GitHubService _github = new();
    private readonly List<VariantChoice> _choices;
    private readonly FieldValidator _v;
    private readonly bool _ready;
    private CancellationTokenSource? _branchCts;

    public FullPipelineWindow(PipelineDialogModel model)
    {
        InitializeComponent();

        KeystoreBox.Text = model.Signing.KeystorePath;
        StorePassBox.Text = model.Signing.StorePassword;
        KeyAliasBox.Text = model.Signing.KeyAlias;
        KeyPassBox.Text = model.Signing.KeyPassword;

        SyncBranchCheck.IsChecked = model.Source.SyncBeforeRun;
        GitHubOwnerBox.Text = model.Source.Owner;
        GitHubRepoBox.Text = model.Source.Repository;
        GitHubTokenBox.Text = model.Source.Token;
        RemoteBox.Text = string.IsNullOrWhiteSpace(model.Source.Remote) ? "origin" : model.Source.Remote;
        BranchBox.Text = string.IsNullOrWhiteSpace(model.Source.Branch) ? "main" : model.Source.Branch;

        AppIdBox.Text = model.Firebase.AppId;
        JsonBox.Text = model.Firebase.CredentialsJsonPath;
        GroupsBox.Text = model.Deploy.Groups;
        EmailsBox.Text = model.Deploy.Emails;
        NotesBox.Text = model.Deploy.Notes;

        RunUnitCheck.IsChecked = model.RunUnitTests;
        RunInstrumentCheck.IsChecked = model.RunInstrumentTests;
        FailureCombo.SelectedIndex = (int)model.FailureAction;

        var selected = new HashSet<string>(model.Signing.SelectedVariants, StringComparer.OrdinalIgnoreCase);
        _choices = model.AvailableVariants.Select(v => new VariantChoice
        {
            Name = v,
            IsSelected = selected.Contains(v),
            Changed = () => { if (_ready) _v.Run(); }
        }).ToList();
        VariantList.ItemsSource = _choices;

        bool SyncOn() => SyncBranchCheck.IsChecked == true;

        _v = new FieldValidator(RunBtn, SummaryText)
            .Rule(KeystoreBox, KeystoreErr, t => FieldValidator.FileExists(t, "Keystore"))
            .Rule(StorePassBox, StorePassErr, t => FieldValidator.Required(t, "Keystore password"))
            .Rule(KeyAliasBox, KeyAliasErr, t => FieldValidator.Required(t, "Key alias"))
            .Rule(KeyPassBox, KeyPassErr, t => FieldValidator.Required(t, "Key password"))
            .Rule(AppIdBox, AppIdErr, t =>
                t.Trim().Split(':') is { Length: 4 } p && p[0] == "1" && p[2] == "android"
                    ? null : "Expected format 1:<project-number>:android:<app-hash>.")
            .Rule(JsonBox, JsonErr, ValidateServiceAccount)
            .Rule(GroupsBox, GroupsErr, _ =>
                string.IsNullOrWhiteSpace(GroupsBox.Text) && string.IsNullOrWhiteSpace(EmailsBox.Text)
                    ? "Enter at least one tester group or email." : null)
            .Rule(EmailsBox, EmailsErr, t =>
                string.IsNullOrWhiteSpace(t) ? null : FieldValidator.Email(t, "Tester emails"))
            .Rule(VariantList, VariantErr, () =>
                _choices.Any(c => c.IsSelected) ? null : "Select at least one variant to build.")
            .Rule(RemoteBox, SourceErr, () =>
            {
                if (!SyncOn()) return null;
                if (!GitNames.IsValid(RemoteBox.Text)) return "Remote name is not valid.";
                if (!GitNames.IsValid(BranchBox.Text)) return "Branch name is not valid.";
                return null;
            }, SyncOn);

        _ready = true;
        UpdateGitHubVisibility();
        UpdateFailureVisibility();
        _v.Run();
        UpdateDeployVariantInfo();
    }

    private static string? ValidateServiceAccount(string v)
    {
        v = v.Trim();
        if (v.Length == 0) return "Service-account JSON is required.";
        if (!File.Exists(v)) return $"File not found: {v}";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(v));
            if (doc.RootElement.TryGetProperty("project_info", out _))
                return "This is google-services.json, not a service-account key.";
            if (!doc.RootElement.TryGetProperty("type", out var t) || t.GetString() != "service_account")
                return "Not a service-account key (missing \"type\": \"service_account\").";
            return null;
        }
        catch (JsonException ex) { return $"Not valid JSON: {ex.Message}"; }
        catch (IOException ex) { return $"Cannot read file: {ex.Message}"; }
    }

    private void Field_Changed(object? s, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _v.Run();
        UpdateDeployVariantInfo();
    }

    private void UpdateDeployVariantInfo()
    {
        var sel = _choices.Where(c => c.IsSelected).Select(c => c.Name).ToList();
        DeployVariantInfo.Text = sel.Count switch
        {
            0 => "",
            1 => $"'{sel[0]}' will be built and deployed.",
            _ => $"Building {sel.Count} variants; only '{sel[0]}' will be deployed to Firebase."
        };
    }

    private void SyncBranch_Changed(object? s, RoutedEventArgs e)
    {
        UpdateGitHubVisibility();
        if (_ready) _v.Run();
    }

    private void UpdateGitHubVisibility() =>
        GitHubPanel.IsVisible = SyncBranchCheck.IsChecked == true;

    private void TestCheck_Changed(object? s, RoutedEventArgs e)
    {
        UpdateFailureVisibility();
        if (_ready) _v.Run();
    }

    private void UpdateFailureVisibility()
    {
        bool any = RunUnitCheck.IsChecked == true || RunInstrumentCheck.IsChecked == true;
        FailurePanel.IsVisible = any;
        InstrumentWarn.IsVisible = RunInstrumentCheck.IsChecked == true;
    }

    private async void LoadBranches_Click(object? s, RoutedEventArgs e)
    {
        string owner = GitHubOwnerBox.Text?.Trim() ?? "";
        string repo = GitHubRepoBox.Text?.Trim() ?? "";
        if (owner.Length == 0 || repo.Length == 0)
        {
            BranchStatusText.Text = "Enter owner and repository first.";
            BranchStatusText.Foreground = this.FindResource("RedBrush") as Avalonia.Media.IBrush;
            return;
        }

        _branchCts?.Cancel();
        _branchCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        LoadBranchesBtn.IsEnabled = false;
        BranchStatusText.Foreground = this.FindResource("Subtext0Brush") as Avalonia.Media.IBrush;
        BranchStatusText.Text = "Loading branches…";
        try
        {
            var branches = await _github.GetBranchesAsync(
                owner, repo, GitHubTokenBox.Text ?? "", _branchCts.Token);
            BranchCombo.ItemsSource = branches;
            string want = BranchBox.Text?.Trim() ?? "";
            BranchCombo.SelectedItem =
                branches.FirstOrDefault(b => string.Equals(b, want, StringComparison.OrdinalIgnoreCase))
                ?? branches.FirstOrDefault();
            if (BranchCombo.SelectedItem is string b) BranchBox.Text = b;

            BranchStatusText.Foreground = this.FindResource(
                branches.Count == 0 ? "YellowBrush" : "GreenBrush") as Avalonia.Media.IBrush;
            BranchStatusText.Text = branches.Count == 0
                ? "No branches found — check the repository name."
                : $"✔ Loaded {branches.Count} branch(es).";
        }
        catch (OperationCanceledException)
        {
            BranchStatusText.Foreground = this.FindResource("YellowBrush") as Avalonia.Media.IBrush;
            BranchStatusText.Text = "Timed out loading branches.";
        }
        catch (Exception ex)
        {
            BranchStatusText.Foreground = this.FindResource("RedBrush") as Avalonia.Media.IBrush;
            BranchStatusText.Text = Toaster.Friendly(ex);
        }
        finally { LoadBranchesBtn.IsEnabled = true; _v.Run(); }
    }

    private void BranchCombo_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (BranchCombo.SelectedItem is string b) BranchBox.Text = b;
    }

    private async void BrowseKeystore_Click(object? s, RoutedEventArgs e)
    {
        var p = await Dialogs.PickFileAsync(this, "Select keystore", KeystoreBox.Text, Dialogs.KeystoreFilters);
        if (p != null) KeystoreBox.Text = p;
    }

    private async void BrowseJson_Click(object? s, RoutedEventArgs e)
    {
        var p = await Dialogs.PickFileAsync(this, "Select service-account JSON", JsonBox.Text, Dialogs.JsonFilters);
        if (p != null) JsonBox.Text = p;
    }

    private async void Run_Click(object? s, RoutedEventArgs e)
    {
        if (!_v.Run()) return;

        if (RunInstrumentCheck.IsChecked == true &&
            !await Dialogs.ConfirmAsync(this, "Instrument tests will wipe app data",
                "connectedAndroidTest uninstalls the app when it finishes, deleting all of its " +
                "data on the target device (saved data, API keys, settings).\n\nContinue?",
                yes: "Run pipeline", danger: true))
            return;

        Close(new PipelineDialogResult(
            new SigningDraft(
                KeystoreBox.Text!.Trim(), StorePassBox.Text ?? "",
                KeyAliasBox.Text!.Trim(), KeyPassBox.Text ?? "",
                [.. _choices.Where(c => c.IsSelected).Select(c => c.Name)]),
            new FirebaseConfigDraft(AppIdBox.Text!.Trim(), JsonBox.Text!.Trim(), "firebase"),
            new DeployTargets(GroupsBox.Text ?? "", EmailsBox.Text ?? "", NotesBox.Text ?? ""),
            new GitSourceDraft(
                SyncBranchCheck.IsChecked == true,
                string.IsNullOrWhiteSpace(RemoteBox.Text) ? "origin" : RemoteBox.Text!.Trim(),
                BranchBox.Text?.Trim() ?? "",
                GitHubOwnerBox.Text?.Trim() ?? "",
                GitHubRepoBox.Text?.Trim() ?? "",
                GitHubTokenBox.Text ?? ""),
            RunUnitCheck.IsChecked == true,
            RunInstrumentCheck.IsChecked == true,
            (TestFailureAction)Math.Max(0, FailureCombo.SelectedIndex)));
    }

    private void Cancel_Click(object? s, RoutedEventArgs e) => Close(null);

    protected override void OnClosed(EventArgs e)
    {
        _branchCts?.Cancel();
        base.OnClosed(e);
    }
}