namespace OFPlayer.WinUI.Core.Playback;

/// <summary>Match the selected ephemeral queue item to the native loaded source.
/// Upload progress does not determine whether a local original can be controlled.</summary>
public static class ExternalQueuePlaybackState
{
    public static bool IsLoaded(string? activeTrackId, string? selectedItemId, bool localOriginal, bool loading)
    {
        if (loading || string.IsNullOrEmpty(selectedItemId)) return false;
        var expected = localOriginal ? "external" : "remote/" + selectedItemId;
        return string.Equals(activeTrackId, expected, StringComparison.Ordinal);
    }

    /// <summary>Actual loaded origin; selection, loading and an expired transport have no origin yet.</summary>
    public static bool? LocalSource(string? activeTrackId, string? selectedItemId, bool localOriginal, bool loading, bool remoteAvailable)
        => !IsLoaded(activeTrackId, selectedItemId, localOriginal, loading) ? null
            : localOriginal ? true : remoteAvailable ? false : null;
}
