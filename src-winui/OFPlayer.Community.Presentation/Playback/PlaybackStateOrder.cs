using System.Text.Json;

namespace OFPlayer.WinUI.Core.Playback;

/// <summary>
/// Orders native playback observations on the UI thread. Progress may update only
/// the source established by a full snapshot; it cannot select a track or undo a
/// newer seek/pause. Reset when replacing the native-host connection.
/// </summary>
public sealed class PlaybackStateOrder
{
    private ulong? revision;
    private string? trackId;

    public void Reset()
    {
        revision = null;
        trackId = null;
    }

    public bool TryAcceptSnapshot(ulong? incomingRevision, string? incomingTrackId)
    {
        if (revision is ulong current &&
            (incomingRevision is not ulong incoming || incoming < current ||
             incoming == current && incomingTrackId != trackId)) return false;

        revision = incomingRevision;
        trackId = incomingTrackId;
        return true;
    }

    public bool AcceptsProgress(ulong? incomingRevision, string? incomingTrackId) =>
        revision is not null && incomingRevision == revision &&
        trackId is not null && incomingTrackId == trackId;

    public static ulong? ReadRevision(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) &&
        field.ValueKind == JsonValueKind.Number && field.TryGetUInt64(out var result) ? result : null;
}
