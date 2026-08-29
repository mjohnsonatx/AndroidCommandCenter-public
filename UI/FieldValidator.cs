using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;

namespace AndroidCommandCenter.UI;

/// <summary>
/// Declarative per-field validation for dialogs.
///
///   var v = new FieldValidator(SaveBtn, SummaryText);
///   v.Rule(AppIdBox, AppIdErr, t => t.Contains(':') ? null : "Format is 1:123:android:abc");
///   v.Run();
///
/// Rules return null when valid, or an error message. Running applies the
/// "invalid" class to the control, writes the message under it, enables the
/// submit button only when every rule passes, and lists failures in the summary.
/// </summary>
public sealed class FieldValidator
{
    private readonly List<(Control Control, TextBlock Error, Func<string?> Rule, Func<bool> IsActive)> _rules = new();
    private readonly Button? _submit;
    private readonly TextBlock? _summary;

    public FieldValidator(Button? submit = null, TextBlock? summary = null)
    {
        _submit = submit;
        _summary = summary;
    }

    public FieldValidator Rule(TextBox box, TextBlock error, Func<string, string?> rule, Func<bool>? when = null)
    {
        _rules.Add((box, error, () => rule(box.Text ?? ""), when ?? (() => true)));
        return this;
    }

    public FieldValidator Rule(Control control, TextBlock error, Func<string?> rule, Func<bool>? when = null)
    {
        _rules.Add((control, error, rule, when ?? (() => true)));
        return this;
    }

    /// <summary>Returns true when everything is valid.</summary>
    public bool Run()
    {
        var failures = new List<string>();
        foreach (var (control, error, rule, isActive) in _rules)
        {
            string? msg = null;
            if (isActive())
            {
                try { msg = rule(); }
                catch (Exception ex) { msg = ex.Message; }
            }
            bool bad = msg != null;
            control.Classes.Set("invalid", bad);
            error.Text = msg ?? "";
            error.IsVisible = bad;
            if (bad) failures.Add(msg!);
        }

        bool ok = failures.Count == 0;
        if (_submit != null) _submit.IsEnabled = ok;
        if (_summary != null)
        {
            _summary.IsVisible = !ok;
            _summary.Text = ok ? "" : failures.Count == 1
                ? failures[0]
                : $"{failures.Count} fields need attention.";
        }
        return ok;
    }

    public IReadOnlyList<string> Errors() =>
        _rules.Where(r => r.IsActive()).Select(r => r.Rule()).Where(m => m != null).ToList()!;

    // ── Reusable rules ─────────────────────────────────────────────────
    public static string? Required(string v, string what = "This field") =>
        string.IsNullOrWhiteSpace(v) ? $"{what} is required." : null;

    public static string? FileExists(string v, string what = "File") =>
        string.IsNullOrWhiteSpace(v) ? $"{what} is required."
        : !System.IO.File.Exists(v.Trim()) ? $"{what} not found: {v.Trim()}"
        : null;

    public static string? GitName(string v, string what = "Name") =>
        Models.GitNames.IsValid(v) ? null
        : string.IsNullOrWhiteSpace(v) ? $"{what} is required."
        : $"{what} contains characters git won't accept.";

    public static string? Port(string v) =>
        int.TryParse(v, out var p) && p is > 0 and <= 65535 ? null : "Port must be 1–65535.";

    public static string? Email(string v, string what = "Email") =>
        string.IsNullOrWhiteSpace(v) ? $"{what} is required."
        : v.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
           .All(a => a.Contains('@') && a.IndexOf('@') < a.LastIndexOf('.'))
            ? null : $"{what} must be one or more valid addresses.";
}