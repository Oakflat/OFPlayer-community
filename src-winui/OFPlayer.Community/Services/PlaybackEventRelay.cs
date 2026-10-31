using System.Text.Json;

namespace OFPlayer_WinUI.Services;
/// <summary>
/// N3: rebroadcasts playback snapshots from the single host event loop to
/// WebView2 consumers (the immersive window). Subscribers never talk to the
/// host directly - the relay only mirrors raw host snapshots, so queue/clock
/// semantics stay owned by the main view model.
/// </summary>
internal static class PlaybackEventRelay
{
    public static event Action<JsonElement>? PlaybackChanged;
    public static event Action<JsonElement, long>? MeterChanged;
    public static void PublishMeter(JsonElement frame, long sequence) => MeterChanged?.Invoke(frame, sequence);
    public static void Publish(JsonElement snapshot)
    {
        PlaybackChanged?.Invoke(snapshot);
    }
}
