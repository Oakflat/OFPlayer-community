namespace OFPlayer.WinUI.Core.Playback;

/// <summary>
/// Owns the local seek preview until the latest command finishes. Pointer moves
/// never issue commands; keyboard/click targets coalesce behind one active seek.
/// All calls are made on the UI thread.
/// </summary>
public sealed class PlaybackSeekInteraction
{
    public sealed record Request(long Id, string TrackId, double Seconds);
    private string? trackId;
    private bool canceled;
    private long sequence;
    private Request? pending;
    private Request? inFlight;
    public bool PointerActive { get; private set; }
    public double? PreviewSeconds { get; private set; }

    public void ObserveTrack(string? currentTrackId)
    {
        if (trackId == currentTrackId) return;
        trackId = currentTrackId;
        canceled = PointerActive;
        pending = null;
        PreviewSeconds = null;
    }

    public void BeginPointer(string currentTrackId, double seconds)
    {
        ObserveTrack(currentTrackId);
        PointerActive = true;
        canceled = false;
        pending = null; // A track click can raise ValueChanged before PointerPressed.
        PreviewSeconds = seconds;
    }

    public void Change(string currentTrackId, double seconds)
    {
        ObserveTrack(currentTrackId);
        if (canceled || !double.IsFinite(seconds)) return;
        PreviewSeconds = Math.Max(0, seconds);
        if (!PointerActive) pending = new(++sequence, currentTrackId, PreviewSeconds.Value);
    }

    public void EndPointer(string? currentTrackId)
    {
        ObserveTrack(currentTrackId);
        if (!PointerActive) return; // Released and CaptureLost may both arrive.
        PointerActive = false;
        if (!canceled && trackId is not null && PreviewSeconds is double seconds)
            pending = new(++sequence, trackId, seconds);
        else PreviewSeconds = null;
        canceled = false;
    }

    public void Cancel()
    {
        PointerActive = false;
        canceled = false;
        pending = null;
        PreviewSeconds = null;
    }

    /// <summary>Esc cancels unsent input, not a seek already owned by the decoder.</summary>
    public bool CancelPendingInput()
    {
        if (!PointerActive && pending is null) return false;
        canceled = PointerActive;
        pending = null;
        PreviewSeconds = null;
        // Keep the pointer gesture fenced until release: Slider may still emit
        // ValueChanged while the mouse is down after Escape.
        return true;
    }

    public Request? TakePending()
    {
        if (PointerActive || inFlight is not null || pending is null) return null;
        inFlight = pending;
        pending = null;
        return inFlight;
    }

    public void Complete(Request request)
    {
        if (inFlight?.Id != request.Id) return;
        inFlight = null;
        if (!PointerActive && pending is null) PreviewSeconds = null;
    }
}
