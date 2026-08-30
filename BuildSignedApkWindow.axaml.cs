using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using AndroidCommandCenter.Models;
using AndroidCommandCenter.UI;

namespace AndroidCommandCenter;

public partial class BuildSignedApkWindow : Window
{
    private readonly List<VariantChoice> _choices;
    private readonly FieldValidator _v;
    private readonly bool _ready;

    public BuildSignedApkWindow() : this(
        new SigningDraft("", "", "", "", ImmutableArray<string>.Empty), [], null) { }

    public BuildSignedApkWindow(SigningDraft draft, IEnumerable<string> availableVariants, string? activeVariant)
    {
        InitializeComponent();

        KeystoreBox.Text = draft.KeystorePath;
        StorePassBox.Text = draft.StorePassword;
        KeyAliasBox.Text = draft.KeyAlias;
        KeyPassBox.Text = draft.KeyPassword;

        var all = availableVariants.ToList();
        var selected = !string.IsNullOrWhiteSpace(activeVariant) && all.Contains(activeVariant)
            ? new HashSet<string>([activeVariant!], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(draft.SelectedVariants, StringComparer.OrdinalIgnoreCase);

        _choices = all.Select(v => new VariantChoice
        {
            Name = v,
            IsSelected = selected.Contains(v),
            Changed = ValidateChoices
        }).ToList();
        VariantList.ItemsSource = _choices;

        _v = new FieldValidator(GenerateBtn, SummaryText)
            .Rule(KeystoreBox, KeystoreErr, t => FieldValidator.FileExists(t, "Keystore"))
            .Rule(StorePassBox, StorePassErr, t => FieldValidator.Required(t, "Keystore password"))
            .Rule(KeyAliasBox, KeyAliasErr, t => FieldValidator.Required(t, "Key alias"))
            .Rule(KeyPassBox, KeyPassErr, t => FieldValidator.Required(t, "Key password"))
            .Rule(VariantList, VariantErr, () =>
                _choices.Any(c => c.IsSelected) ? null : "Select at least one variant to build.");

        _ready = true;
        _v.Run();
    }

    private void ValidateChoices() { if (_ready) _v.Run(); }

    private void Field_Changed(object? s, TextChangedEventArgs e) { if (_ready) _v.Run(); }

    private void Reveal_Changed(object? s, RoutedEventArgs e)
    {
        char? ch = RevealToggle.IsChecked == true ? null : '●';
        StorePassBox.PasswordChar = ch ?? '\0';
        KeyPassBox.PasswordChar = ch ?? '\0';
        StorePassBox.RevealPassword = RevealToggle.IsChecked == true;
        KeyPassBox.RevealPassword = RevealToggle.IsChecked == true;
    }

    private async void BrowseKeystore_Click(object? s, RoutedEventArgs e)
    {
        var path = await Dialogs.PickFileAsync(this, "Select keystore",
            KeystoreBox.Text, Dialogs.KeystoreFilters);
        if (path != null) KeystoreBox.Text = path;
    }

    /// <summary>
    /// Runs keytool to prove the password and alias are right. Catching this
    /// here turns a 10-minute Gradle failure into a 2-second message.
    /// </summary>
    private async void Verify_Click(object? s, RoutedEventArgs e)
    {
        VerifyBtn.IsEnabled = false;
        VerifyInfo.Foreground = this.FindResource("Subtext0Brush") as IBrush;
        VerifyInfo.Text = "Running keytool…";
        try
        {
            string keytool = Platform.Which("keytool")
                ?? throw new InvalidOperationException(
                    "keytool not found on PATH. Install a JDK (sudo dnf install java-17-openjdk-devel).");

            var psi = new ProcessStartInfo(keytool)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var a in new[]
            {
                "-list", "-v",
                "-keystore", KeystoreBox.Text!.Trim(),
                "-alias", KeyAliasBox.Text!.Trim(),
                "-storepass", StorePassBox.Text ?? "",
                "-J-Duser.language=en"
            }) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            string outp = await p.StandardOutput.ReadToEndAsync();
            string err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();

            if (p.ExitCode == 0)
            {
                string until = outp.Split('\n')
                    .FirstOrDefault(l => l.Contains("until:", StringComparison.OrdinalIgnoreCase))?.Trim()
                    ?? "";
                VerifyInfo.Foreground = this.FindResource("GreenBrush") as IBrush;
                VerifyInfo.Text = $"✔ Keystore and alias verified. {until}";
            }
            else
            {
                string combined = (err + outp).ToLowerInvariant();
                VerifyInfo.Foreground = this.FindResource("RedBrush") as IBrush;
                VerifyInfo.Text =
                    combined.Contains("password was incorrect") || combined.Contains("keystore password")
                        ? "✖ Keystore password is incorrect."
                    : combined.Contains("alias") && combined.Contains("does not exist")
                        ? "✖ That alias does not exist in this keystore."
                    : combined.Contains("invalid keystore format")
                        ? "✖ Not a valid keystore file."
                        : $"✖ keytool exited {p.ExitCode}: {(err + outp).Split('\n').FirstOrDefault()}";
            }
        }
        catch (Exception ex)
        {
            VerifyInfo.Foreground = this.FindResource("RedBrush") as IBrush;
            VerifyInfo.Text = $"✖ {Toaster.Friendly(ex)}";
        }
        finally { VerifyBtn.IsEnabled = true; }
    }

    private void Generate_Click(object? s, RoutedEventArgs e)
    {
        if (!_v.Run()) return;
        Close(new SigningDraft(
            KeystoreBox.Text!.Trim(),
            StorePassBox.Text ?? "",
            KeyAliasBox.Text!.Trim(),
            KeyPassBox.Text ?? "",
            [.. _choices.Where(c => c.IsSelected).Select(c => c.Name)]));
    }

    private void Cancel_Click(object? s, RoutedEventArgs e) => Close(null);
}
