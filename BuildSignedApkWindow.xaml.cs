using System.IO;
using System.Windows;
using System.Windows.Controls;
using AndroidCommandCenter.Models;

namespace AndroidCommandCenter;

public class VariantChoice
{
    public string Name { get; set; } = "";
    public bool IsSelected { get; set; }
}

public partial class BuildSignedApkWindow : Window
{
    private readonly CiCdConfig _cfg;
    private List<VariantChoice> _choices = new();

    public BuildSignedApkWindow(CiCdConfig cfg, IEnumerable<string> availableVariants, string? activeVariant)
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

        Validate();
    }

    private void Field_Changed(object s, TextChangedEventArgs e) => Validate();
    private void Pass_Changed(object s, RoutedEventArgs e) => Validate();
    private void Variant_Changed(object s, RoutedEventArgs e) => Validate();

    private void Validate() =>
        GenerateBtn.IsEnabled =
            !string.IsNullOrWhiteSpace(KeystoreBox.Text) && File.Exists(KeystoreBox.Text) &&
            StorePassBox.Password.Length > 0 &&
            !string.IsNullOrWhiteSpace(KeyAliasBox.Text) &&
            KeyPassBox.Password.Length > 0 &&
            _choices.Any(c => c.IsSelected);

    private void BrowseKeystore_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Keystore (*.jks;*.keystore)|*.jks;*.keystore|All files|*.*" };
        if (dlg.ShowDialog() == true) { KeystoreBox.Text = dlg.FileName; Validate(); }
    }

    private void Generate_Click(object s, RoutedEventArgs e)
    {
        _cfg.KeystorePath = KeystoreBox.Text.Trim();
        _cfg.StorePassword = StorePassBox.Password;
        _cfg.KeyAlias = KeyAliasBox.Text.Trim();
        _cfg.KeyPassword = KeyPassBox.Password;
        _cfg.BuildVariants = _choices.Where(c => c.IsSelected).Select(c => c.Name).ToList();
        DialogResult = true; Close();
    }

    private void Cancel_Click(object s, RoutedEventArgs e) { DialogResult = false; Close(); }
}
