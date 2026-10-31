namespace OFPlayer_WinUI.Models;
using Microsoft.UI.Xaml;

/// <summary>
/// One lyric line row for the native main-window panel (N3), carrying the same
/// fidelity the old front-end model had (`src/models/lyrics.ts`).
///
/// Fidelity note: this row is already the *grouped* display line produced by
/// <see cref = "LyricTimeline"/>. Bilingual LRC files that place the original and
/// the translation at (nearly) the same timestamp are folded into a single row,
/// with the secondary text moved into <see cref = "Translation"/>/<see cref = "Roman"/>.
/// </summary>
public sealed record LyricLineRow(int Index, string Text, string Translation, bool IsActive) : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>Observable so the active line changes emphasis in place (no row re-creation).</summary>
    private bool isActive = IsActive;
    public bool IsActive
    {
        get => isActive;
        set
        {
            if (isActive == value)
                return;
            isActive = value;
            PropertyChanged?.Invoke(this, new(nameof(IsActive)));
            PropertyChanged?.Invoke(this, new(nameof(TextOpacity)));
            PropertyChanged?.Invoke(this, new(nameof(TranslationOpacity)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Romanized (romanLyric) secondary text; empty when absent.</summary>
    public string Roman { get; init; } = string.Empty;
    public IReadOnlyList<OFPlayer.WinUI.Core.Lyrics.LyricTimeline.Word> Words { get; init; } = Array.Empty<OFPlayer.WinUI.Core.Lyrics.LyricTimeline.Word>();
    public bool IsBackground { get; init; }
    public bool IsDuet { get; init; }
    public string? Agent { get; init; }
    public string RoleLabel => IsBackground ? Services.Localization.Get("lyrics.role.background") : IsDuet ? Services.Localization.Get("lyrics.role.duet") + (string.IsNullOrWhiteSpace(Agent) ? string.Empty : $" · {Agent}") : string.Empty;
    public Visibility RoleVisibility => RoleLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Line start in seconds; null for unsynced (plain-text) lyrics.</summary>
    public double? StartSeconds { get; init; }
    /// <summary>
    /// Effective line end in seconds. Explicit LRC end wins when it is sane,
    /// otherwise the next line's start, otherwise start + fallback (see LyricTimeline).
    /// </summary>
    public double? EndSeconds { get; init; }
    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translation);
    public bool HasRoman => !string.IsNullOrWhiteSpace(Roman);
    /// <summary>True when either secondary track exists (old `isBilingual`).</summary>
    public bool HasSecondary => HasTranslation || HasRoman;
    // Template-friendly emphasis without a converter.
    public double TextOpacity => IsActive ? 1.0 : 0.42;
    /// <summary>Applied (and tweened) by the lyrics view; not bound, so it can animate.</summary>
    public double TextSize => IsActive ? 22 : 15;
    public double TranslationOpacity => IsActive ? 0.85 : 0.3;
    // Template-friendly visibility without a converter: an empty secondary track
    // collapses its row instead of leaving a blank gap. Kept in sync with the
    // Has* flags above so XAML can bind directly.
    public Visibility TranslationVisibility => HasTranslation ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RomanVisibility => HasRoman ? Visibility.Visible : Visibility.Collapsed;
}
