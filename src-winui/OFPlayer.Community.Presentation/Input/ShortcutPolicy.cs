namespace OFPlayer.WinUI.Core.Input;

/// <summary>What currently has the keyboard focus, as far as shortcuts care.</summary>
public enum FocusKind
{
    /// <summary>Nothing in particular: the window itself or a plain container.</summary>
    Nothing,
    /// <summary>A box the listener types in. Keys belong to it.</summary>
    TextEntry,
    /// <summary>A control that uses Space or the arrows for itself (button, switch, slider, menu, list of settings).</summary>
    Activator,
    /// <summary>A row of a song list. Space would otherwise play that row again.</summary>
    SongRow,
    /// <summary>Anything else that is not interactive.</summary>
    Other,
}

/// <summary>
/// When the player's own keys apply. They never take a key from a box being typed in, from a control that
/// needs it, or from a dialog or menu that is open over the window; and Space is not a play button while
/// songs are being selected, because there it ticks a row.
/// </summary>
public static class ShortcutPolicy
{
    public static bool SpaceTogglesPlayback(FocusKind focus, bool selectingSongs, bool somethingOpen) =>
        !selectingSongs && !somethingOpen && focus is FocusKind.Nothing or FocusKind.SongRow or FocusKind.Other;

    public static bool CtrlArrowSkips(FocusKind focus, bool somethingOpen) =>
        !somethingOpen && focus != FocusKind.TextEntry;

    public static bool CtrlFSearches(bool somethingOpen) => !somethingOpen;
}
