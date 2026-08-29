using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace AndroidCommandCenter.UI;

public static class Dialogs
{
    public static Task InfoAsync(Window owner, string title, string? message = null) =>
        MessageDialog.ShowAsync(owner, new(title, message));

    public static Task SuccessAsync(Window owner, string title, string? message = null, string? details = null) =>
        MessageDialog.ShowAsync(owner, new(title, message, Details: details, Severity: ToastSeverity.Success));

    public static Task WarningAsync(Window owner, string title, string? message = null, string? details = null) =>
        MessageDialog.ShowAsync(owner, new(title, message, Details: details, Severity: ToastSeverity.Warning));

    public static Task ErrorAsync(Window owner, string title, string? message = null, string? details = null) =>
        MessageDialog.ShowAsync(owner, new(title, message, Details: details, Severity: ToastSeverity.Error));

    /// <summary>Error with a bulleted list of problems — used for validation failures.</summary>
    public static Task ErrorAsync(Window owner, string title, IEnumerable<string> problems, string? details = null) =>
        MessageDialog.ShowAsync(owner, new(title, "Fix the following and try again:",
            Bullets: problems, Details: details, Severity: ToastSeverity.Error));

    public static Task ErrorAsync(Window owner, string title, Exception ex) =>
        MessageDialog.ShowAsync(owner, new(title, Toaster.Friendly(ex), Details: ex.ToString(),
            Severity: ToastSeverity.Error));

    public static Task<bool> ConfirmAsync(Window owner, string title, string message,
        string yes = "Yes", string no = "Cancel", bool danger = false) =>
        MessageDialog.ShowAsync(owner, new(title, message,
            Severity: danger ? ToastSeverity.Warning : ToastSeverity.Info,
            PrimaryLabel: yes, SecondaryLabel: no, PrimaryIsDanger: danger));

    /// <summary>Destructive confirm that requires ticking a checkbox.</summary>
    public static Task<bool> ConfirmDestructiveAsync(Window owner, string title, string message,
        string checkboxText, string yes = "Proceed") =>
        MessageDialog.ShowAsync(owner, new(title, message, Severity: ToastSeverity.Warning,
            PrimaryLabel: yes, SecondaryLabel: "Cancel", PrimaryIsDanger: true,
            ConfirmCheckboxText: checkboxText));

    // ── File pickers ──────────────────────────────────────────────────

    public static async Task<string?> PickFileAsync(Window owner, string title,
        string? startPath = null, params (string Name, string[] Patterns)[] filters)
    {
        var sp = owner.StorageProvider;
        if (!sp.CanOpen) return null;

        var opts = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = filters.Length == 0
                ? null
                : filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns }).ToList()
        };

        var startDir = StartDir(startPath);
        if (startDir != null)
            opts.SuggestedStartLocation = await sp.TryGetFolderFromPathAsync(startDir);
        if (!string.IsNullOrEmpty(startPath) && File.Exists(startPath))
            opts.SuggestedFileName = Path.GetFileName(startPath);

        var result = await sp.OpenFilePickerAsync(opts);
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task<string?> PickFolderAsync(Window owner, string title, string? startPath = null)
    {
        var sp = owner.StorageProvider;
        if (!sp.CanPickFolder) return null;

        var opts = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        var startDir = StartDir(startPath);
        if (startDir != null)
            opts.SuggestedStartLocation = await sp.TryGetFolderFromPathAsync(startDir);

        var result = await sp.OpenFolderPickerAsync(opts);
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    private static string? StartDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path);
        return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null;
    }

    // Common filter sets
    public static readonly (string, string[])[] KeystoreFilters =
        [("Keystore", ["*.jks", "*.keystore"]), ("All files", ["*"])];
    public static readonly (string, string[])[] JsonFilters =
        [("JSON", ["*.json"]), ("All files", ["*"])];
    public static readonly (string, string[])[] ApkFilters =
        [("APK", ["*.apk"]), ("All files", ["*"])];
}