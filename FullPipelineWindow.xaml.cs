using System.IO;
using System.Windows;
using System.Windows.Controls;
using AndroidCommandCenter.Models;

namespace AndroidCommandCenter;

public partial class FullPipelineWindow : Window
{
    private readonly CiCdConfig _cfg;
    private List<VariantChoice> _choices = new();

    public FullPipelineWindow(CiCdConfig cfg, IEnumerable<string> availableVariants, string? activeVariant)
    {
        InitializeComponent();
        _cfg = cfg;

        KeystoreBox.Text = cfg.KeystorePath;
        StorePassBox.Password = cfg.StorePassword;
        KeyAliasBox.Text = cfg.KeyAlias;
        KeyPassBox.Password = cfg.KeyPassword;

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
        Validate();
    }

    private void Field_Changed(object s, TextChangedEventArgs e) => Validate();
    private void Pass_Changed(object s, RoutedEventArgs e) => Validate();
    private void Variant_Changed(object s, RoutedEventArgs e) => Validate();

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
        RunBtn.IsEnabled = signing && firebase && variant;
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
}
