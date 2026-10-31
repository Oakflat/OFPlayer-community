using System.Text.Json;

namespace OFPlayer_WinUI.ViewModels;
public sealed partial class MainViewModel
{
    public bool CanEditPlaybackLyrics => IsConnected && CurrentTrackDetail is not null;

    // Room catalog items carry metadata and covers, but no lyric payload. Every
    // playback surface uses this boundary so a peer ID never probes our catalog.
    public Task<JsonElement> ResolvePlaybackLyricsAsync(string trackId, double? positionSeconds)
    {
        var activeClient = client ?? throw new InvalidOperationException("Native host is disconnected.");
        return activeClient.SendAsync("lyrics_resolve_track", new { trackId, positionSeconds }, CancellationToken.None);
    }
}
