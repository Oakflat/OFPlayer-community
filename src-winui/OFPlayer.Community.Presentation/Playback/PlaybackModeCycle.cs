namespace OFPlayer.WinUI.Core.Playback;

/// <summary>The playback bar's four modes, in the same order as the legacy player.</summary>
public static class PlaybackModeCycle
{
    public static (string RepeatMode, bool ShuffleEnabled) Next(string repeatMode, bool shuffleEnabled)
    {
        if (shuffleEnabled)
        {
            return (PlaybackOrder.RepeatNone, false);
        }

        return repeatMode switch
        {
            PlaybackOrder.RepeatNone => (PlaybackOrder.RepeatAll, false),
            PlaybackOrder.RepeatAll => (PlaybackOrder.RepeatOne, false),
            _ => (PlaybackOrder.RepeatNone, true),
        };
    }
}
