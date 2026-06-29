using System.IO;
using System.Windows;
using System.Windows.Controls;
using AndroidCommandCenter.Models;
using AndroidCommandCenter.Services;

namespace AndroidCommandCenter;

public partial class FullPipelineWindow : Window
{
    private readonly CiCdConfig _cfg;
    private readonly GitHubService _github = new();
    private List<VariantChoice> _choices = new();

    public FullPipelineWindow(CiCdConfig cfg, IEnumerable<string> availableVariants, string? activeVariant)
    {
        InitializeComponent();
        _cfg = cfg;

        KeystoreBox.Text = cfg.KeystorePath;
        StorePassBox.Password = cfg.StorePassword;
        KeyAliasBox.Text = cfg.KeyAlias;
        KeyPassBox.Password = cfg.KeyPassword;

        SyncBranchCheck.IsChecked = cfg.SyncBranchBeforePipeline;
        GitHubOwnerBox.Text = cfg.GitHubOwner;
        GitHubRepoBox.Text = cfg.GitHubRepository;
        GitHubTokenBox.Password = cfg.GitHubToken;
        RemoteBox.Text = string.IsNullOrWhiteSpace(cfg.PipelineRemoteName) ? "origin" : cfg.PipelineRemoteName;
        BranchBox.Text = string.IsNullOrWhiteSpace(cfg.PipelineBranch) ? "master" : cfg.PipelineBranch;

        var variantList = availableVariants.ToList();
        var selectedVariants =
            !string.IsNullOrWhiteSpace(activeVariant) && variantList.Contains(activeVariant)
                ? new HashSet<string>([activeVariant])
                : new HashSet<string>(cfg.BuildVariants);

        _choices = variantList
            .Select(v => new VariantChoice { Name = v, IsSelected = selectedVariants.Contains(v) })
            .ToList();
        VariantList.ItemsSource = _choices;

        AppIdBox.Text = cfg.FirebaseAppId;
        JsonBox.Text = cfg.CredentialsJsonPath;
        GroupsBox.Text = cfg.TesterGroups;
        EmailsBox.Text = cfg.TesterEmails;
        NotesBox.Text = cfg.ReleaseNotes;

        RunUnitCheck.IsChecked = cfg.RunUnitTests;
        RunInstrumentCheck.IsChecked = cfg.RunInstrumentTests;
        FailureCombo.SelectedIndex = (int)cfg.FailureAction;

        UpdateFailureVisibility();
        UpdateGitHubVisibility();
        Validate();
    }

    private void Field_Changed(object s, TextChangedEventArgs e) => Validate();
    private void Pass_Changed(object s, RoutedEventArgs e) => Validate();
    private void Variant_Changed(object s, RoutedEventArgs e) => Validate();
    private void GitHubSource_Changed(object s, TextChangedEventArgs e) => Validate();
    private void GitHubToken_Changed(object s, RoutedEventArgs e) => Validate();

    private void SyncBranch_Changed(object s, RoutedEventArgs e)
    {
        UpdateGitHubVisibility();
        Validate();
    }

    private void UpdateGitHubVisibility() =>
        GitHubPanel.Visibility = SyncBranchCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private async void LoadBranches_Click(object s, RoutedEventArgs e)
    {
        string owner = GitHubOwnerBox.Text.Trim();
        string repo = GitHubRepoBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            BranchStatusText.Text = "Enter owner and repository first.";
            return;
        }

        LoadBranchesBtn.IsEnabled = false;
        BranchStatusText.Text = "Loading branches...";

        try
        {
            var branches = await _github.GetBranchesAsync(
                owner,
                repo,
                GitHubTokenBox.Password,
                CancellationToken.None);

            BranchCombo.ItemsSource = branches;
            string selectedBranch = BranchBox.Text.Trim();
            BranchCombo.SelectedItem = branches.Contains(selectedBranch, StringComparer.OrdinalIgnoreCase)
                ? branches.First(b => string.Equals(b, selectedBranch, StringComparison.OrdinalIgnoreCase))
                : branches.FirstOrDefault();

            if (BranchCombo.SelectedItem is string branch)
                BranchBox.Text = branch;

            BranchStatusText.Text = branches.Count == 0
                ? "No branches found."
                : $"Loaded {branches.Count} branch(es).";
        }
        catch (Exception ex)
        {
            BranchStatusText.Text = ex.Message;
        }
        finally
        {
            LoadBranchesBtn.IsEnabled = true;
            Validate();
        }
    }

    private void BranchCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (BranchCombo.SelectedItem is string branch)
            BranchBox.Text = branch;
    }

    private void TestCheck_Changed(object s, RoutedEventArgs e)
    {
        UpdateFailureVisibility();
        Validate();
    }

    private void UpdateFailureVisibility() =>
        FailurePanel.Visibility =
            (RunUnitCheck.IsChecked == true || RunInstrumentCheck.IsChecked == true)
                ? Visibility.Visible : Visibility.Collapsed;

    private void Validate()
    {
        bool signing = !string.IsNullOrWhiteSpace(KeystoreBox.Text) && File.Exists(KeystoreBox.Text)
                    && StorePassBox.Password.Length > 0
                    && !string.IsNullOrWhiteSpace(KeyAliasBox.Text)
                    && KeyPassBox.Password.Length > 0;
        bool firebase = !string.IsNullOrWhiteSpace(AppIdBox.Text)
                     && !string.IsNullOrWhiteSpace(JsonBox.Text);
        bool variant = _choices.Any(c => c.IsSelected);
        bool source = SyncBranchCheck.IsChecked != true ||
                      (IsSimpleGitName(RemoteBox.Text) && IsSimpleGitName(BranchBox.Text));
        RunBtn.IsEnabled = signing && firebase && variant && source;
    }

    private void BrowseKeystore_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Keystore (*.jks;*.keystore)|*.jks;*.keystore|All files|*.*" };
        if (dlg.ShowDialog() == true) { KeystoreBox.Text = dlg.FileName; Validate(); }
    }

    private void BrowseJson_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog() == true) { JsonBox.Text = dlg.FileName; Validate(); }
    }

    private void Run_Click(object s, RoutedEventArgs e)
    {
        _cfg.KeystorePath = KeystoreBox.Text.Trim();
        _cfg.StorePassword = StorePassBox.Password;
        _cfg.KeyAlias = KeyAliasBox.Text.Trim();
        _cfg.KeyPassword = KeyPassBox.Password;
        _cfg.BuildVariants = _choices.Where(c => c.IsSelected).Select(c => c.Name).ToList();

        _cfg.SyncBranchBeforePipeline = SyncBranchCheck.IsChecked == true;
        _cfg.GitHubOwner = GitHubOwnerBox.Text.Trim();
        _cfg.GitHubRepository = GitHubRepoBox.Text.Trim();
        _cfg.GitHubToken = GitHubTokenBox.Password;
        _cfg.PipelineRemoteName = string.IsNullOrWhiteSpace(RemoteBox.Text) ? "origin" : RemoteBox.Text.Trim();
        _cfg.PipelineBranch = BranchBox.Text.Trim();

        _cfg.FirebaseAppId = AppIdBox.Text.Trim();
        _cfg.CredentialsJsonPath = JsonBox.Text.Trim();
        _cfg.TesterGroups = GroupsBox.Text.Trim();
        _cfg.TesterEmails = EmailsBox.Text.Trim();
        _cfg.ReleaseNotes = NotesBox.Text;

        _cfg.RunUnitTests = RunUnitCheck.IsChecked == true;
        _cfg.RunInstrumentTests = RunInstrumentCheck.IsChecked == true;
        _cfg.FailureAction = (TestFailureAction)Math.Max(0, FailureCombo.SelectedIndex);

        DialogResult = true; Close();
    }

    private void Cancel_Click(object s, RoutedEventArgs e) { DialogResult = false; Close(); }

    private static bool IsSimpleGitName(string value)
    {
        value = value.Trim();
        return !string.IsNullOrWhiteSpace(value) &&
               !value.Any(char.IsWhiteSpace) &&
               !value.Contains('"') &&
               !value.Contains('\'');
    }
}
