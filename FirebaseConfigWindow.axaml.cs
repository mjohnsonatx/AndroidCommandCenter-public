using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AndroidCommandCenter.Models;
using AndroidCommandCenter.UI;

namespace AndroidCommandCenter;

public partial class FirebaseConfigWindow : Window
{
    private readonly FieldValidator _v;
    private readonly bool _ready;

    public FirebaseConfigWindow() : this(new FirebaseConfigDraft("", "", "firebase")) { }

    public FirebaseConfigWindow(FirebaseConfigDraft draft)
    {
        InitializeComponent();
        AppIdBox.Text = draft.AppId;
        JsonBox.Text = draft.CredentialsJsonPath;
        CliBox.Text = draft.CliPath;

        _v = new FieldValidator(SaveBtn, SummaryText)
            .Rule(AppIdBox, AppIdErr, ValidateAppId)
            .Rule(JsonBox, JsonErr, ValidateJson)
            .Rule(CliBox, CliErr, t => string.IsNullOrWhiteSpace(t) ? null
                : t.Contains('/') && !File.Exists(t.Trim())
                    ? $"CLI path not found: {t.Trim()}" : null);

        _ready = true;
        _v.Run();
        _ = ProbeCliAsync(quiet: true);
    }

    private void Field_Changed(object? s, TextChangedEventArgs e)
    {
        if (_ready) _v.Run();
    }

    // ── Validation rules ──────────────────────────────────────────────

    private static string? ValidateAppId(string v)
    {
        v = v.Trim();
        if (v.Length == 0) return "App ID is required.";
        // 1:<project-number>:android:<hash>
        var parts = v.Split(':');
        if (parts.Length != 4 || parts[0] != "1" || parts[2] != "android")
            return "Expected format 1:<project-number>:android:<app-hash>.";
        return null;
    }

    private string? ValidateJson(string v)
    {
        JsonInfo.IsVisible = false;
        v = v.Trim();
        if (v.Length == 0) return "Service-account JSON is required.";
        if (!File.Exists(v)) return $"File not found: {v}";

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(v));
            var root = doc.RootElement;

            if (root.TryGetProperty("project_info", out _) || root.TryGetProperty("client", out _))
                return "This is google-services.json (the app config). You need a service-account key from IAM → Service Accounts.";

            if (!root.TryGetProperty("type", out var type) || type.GetString() != "service_account")
                return "Not a service-account key: missing \"type\": \"service_account\".";

            if (!root.TryGetProperty("private_key", out _))
                return "Service-account key is missing its private_key — re-download it.";

            string email = root.TryGetProperty("client_email", out var e) ? e.GetString() ?? "?" : "?";
            string project = root.TryGetProperty("project_id", out var p) ? p.GetString() ?? "?" : "?";
            JsonInfo.Text = $"✔ Service account {email} (project {project})";
            JsonInfo.IsVisible = true;
            return null;
        }
        catch (JsonException ex)
        {
            return $"Not valid JSON: {ex.Message}";
        }
        catch (IOException ex)
        {
            return $"Cannot read file: {ex.Message}";
        }
    }

    // ── CLI probe ─────────────────────────────────────────────────────

    private async void TestCli_Click(object? s, RoutedEventArgs e) => await ProbeCliAsync(quiet: false);

    private async Task ProbeCliAsync(bool quiet)
    {
        string cli = string.IsNullOrWhiteSpace(CliBox.Text) ? "firebase" : CliBox.Text.Trim();
        TestCliBtn.IsEnabled = false;
        CliInfo.Foreground = this.FindResource("Subtext0Brush") as Avalonia.Media.IBrush;
        CliInfo.Text = "Checking Firebase CLI…";
        try
        {
            var psi = new ProcessStartInfo(cli)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            psi.ArgumentList.Add("--version");

            using var p = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string outp = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);

            if (p.ExitCode == 0)
            {
                CliInfo.Foreground = this.FindResource("GreenBrush") as Avalonia.Media.IBrush;
                CliInfo.Text = $"✔ firebase-tools {outp.Trim()}";
            }
            else
            {
                CliInfo.Foreground = this.FindResource("RedBrush") as Avalonia.Media.IBrush;
                CliInfo.Text = $"CLI exited {p.ExitCode}. Run `{cli} login` or check the install.";
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            CliInfo.Foreground = this.FindResource("RedBrush") as Avalonia.Media.IBrush;
            CliInfo.Text = $"'{cli}' not found on PATH. Install: sudo npm i -g firebase-tools";
        }
        catch (OperationCanceledException)
        {
            CliInfo.Foreground = this.FindResource("YellowBrush") as Avalonia.Media.IBrush;
            CliInfo.Text = "CLI did not respond within 15 s (first run can be slow — try again).";
        }
        catch (Exception ex) when (quiet)
        {
            CliInfo.Text = ex.Message;
        }
        finally { TestCliBtn.IsEnabled = true; }
    }

    // ── Buttons ───────────────────────────────────────────────────────

    private async void BrowseJson_Click(object? s, RoutedEventArgs e)
    {
        var path = await Dialogs.PickFileAsync(this, "Select service-account JSON",
            JsonBox.Text, Dialogs.JsonFilters);
        if (path != null) JsonBox.Text = path;
    }

    private void Save_Click(object? s, RoutedEventArgs e)
    {
        if (!_v.Run()) return;   // belt and braces — button should already be disabled
        Close(new FirebaseConfigDraft(
            AppIdBox.Text!.Trim(),
            JsonBox.Text!.Trim(),
            string.IsNullOrWhiteSpace(CliBox.Text) ? "firebase" : CliBox.Text.Trim()));
    }

    private void Cancel_Click(object? s, RoutedEventArgs e) => Close(null);
}
