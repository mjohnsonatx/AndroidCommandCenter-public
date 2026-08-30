using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AndroidCommandCenter.UI;

/// <summary>
/// Needs INotifyPropertyChanged: Avalonia TwoWay bindings to a plain CLR
/// property write back, but nothing else observing the list would see it, and
/// the old WPF version silently failed to round-trip in some templates.
/// </summary>
public sealed class VariantChoice : INotifyPropertyChanged
{
    public string Name { get; init; } = "";

    private bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; OnChanged(); Changed?.Invoke(); } }
    }

    /// <summary>Raised so the dialog can re-validate without per-item handlers.</summary>
    public Action? Changed { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}