using System.Text.Json.Nodes;

namespace OFPlayer.WinUI.Core.Playback;

/// <summary>
/// WebView2 event frame shared by the capsule host and page.
/// The page receives { event, data } directly; playback data is never nested
/// in a callback or Channel wrapper.
/// </summary>
public static class CapsuleEventEnvelope
{
    public static JsonObject Build(string eventName, JsonNode? payload) => new()
    {
        ["event"] = eventName,
        ["data"] = payload?.DeepClone(),
    };
}
