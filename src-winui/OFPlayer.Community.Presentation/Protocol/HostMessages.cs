using System.Text.Json;
using System.Text.Json.Serialization;

namespace OFPlayer.WinUI.Core.Protocol;

/// <summary>Handshake payload returned by the native host.</summary>
public sealed record HandshakeInfo(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("hostVersion")] string HostVersion,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("dataRoot")] string DataRoot,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("startedAtMs")] long StartedAtMs)
{
    public bool Supports(string capability) => Capabilities.Contains(capability);
}

/// <summary>Structured protocol error returned by the host.</summary>
public sealed record HostError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Event envelope: playback.changed, playback.progress, playback.ended, session.changed.
/// Events carry the host sessionId and a monotonic sequence so a reconnected client
/// can discard events from an older session.
/// </summary>
public sealed record HostEvent(
    [property: JsonPropertyName("event")] string Name,
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("seq")] long Sequence,
    [property: JsonPropertyName("data")] JsonElement Data)
{
    public const string PlaybackChanged = "playback.changed";
    public const string PlaybackProgress = "playback.progress";
    public const string PlaybackMeter = "playback.meter";
    public const string PlaybackEnded = "playback.ended";
    public const string SessionChanged = "session.changed";
    /// <summary>Import worker progress (N1): { phase, processed, total, currentFile, ... }.</summary>
    public const string ImportProgress = "import.progress";
    /// <summary>M1b storage watcher: { kind, paths } for watched library directories.</summary>
    public const string StorageWatchChanged = "storage.watch-changed";

    /// <summary>Critical events are never dropped; progress and meter frames may be coalesced.</summary>
    public bool IsCritical => Name is not (PlaybackProgress or PlaybackMeter);
}

/// <summary>Common serialization options for the host protocol.</summary>
public static class HostJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
