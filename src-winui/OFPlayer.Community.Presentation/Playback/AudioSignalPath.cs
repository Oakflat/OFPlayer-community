using System.Text.Json;

namespace OFPlayer.WinUI.Core.Playback;

/// <summary>One end of the host's signal path. Unknown depth or format stays null.</summary>
public sealed record AudioSignalFormat(uint SampleRate, uint Channels, uint? BitDepth, string? SampleFormat);

/// <summary>
/// Display copy of <c>PlaybackSnapshot.signalPath</c>. The host decides every value;
/// <c>BitPerfect</c> and <c>IntegrityStatus</c> are kept only so nothing is silently
/// dropped, never to show a verified result.
/// </summary>
public sealed record AudioSignalPath(
    AudioSignalFormat Source,
    AudioSignalFormat Output,
    bool Resampled,
    bool ChannelConverted,
    bool SampleFormatConverted,
    bool SoftwareMixer,
    bool SoftwareVolume,
    bool BitPerfect,
    string IntegrityStatus)
{
    /// <summary>
    /// Reads <c>signalPath</c> from a playback snapshot. A missing, null or malformed
    /// path is null: the screen then says the output is unknown instead of guessing.
    /// </summary>
    public static AudioSignalPath? FromSnapshot(JsonElement snapshot) =>
        snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("signalPath", out var path) ? Parse(path) : null;

    public static AudioSignalPath? Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        var source = Format(value, "source");
        var output = Format(value, "output");
        if (source is null || output is null) return null;
        bool? Flag(string name) => value.TryGetProperty(name, out var field) && field.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? field.GetBoolean() : null;
        var resampled = Flag("resampled");
        var channels = Flag("channelConverted");
        var sampleFormat = Flag("sampleFormatConverted");
        var mixer = Flag("softwareMixer");
        var volume = Flag("softwareVolume");
        var bitPerfect = Flag("bitPerfect");
        if (resampled is null || channels is null || sampleFormat is null || mixer is null || volume is null || bitPerfect is null) return null;
        var integrity = value.TryGetProperty("integrityStatus", out var status) && status.ValueKind == JsonValueKind.String
            ? status.GetString() ?? string.Empty : string.Empty;
        return new(source, output, resampled.Value, channels.Value, sampleFormat.Value, mixer.Value, volume.Value, bitPerfect.Value, integrity);
    }

    private static AudioSignalFormat? Format(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) return null;
        if (!value.TryGetProperty("sampleRate", out var rate) || !rate.TryGetUInt32(out var sampleRate) || sampleRate == 0) return null;
        if (!value.TryGetProperty("channels", out var count) || !count.TryGetUInt32(out var channels) || channels == 0) return null;
        uint? bitDepth = value.TryGetProperty("bitDepth", out var depth) && depth.ValueKind == JsonValueKind.Number &&
            depth.TryGetUInt32(out var bits) && bits > 0 ? bits : null;
        var sampleFormat = value.TryGetProperty("sampleFormat", out var format) && format.ValueKind == JsonValueKind.String
            ? format.GetString() : null;
        return new(sampleRate, channels, bitDepth, string.IsNullOrWhiteSpace(sampleFormat) ? null : sampleFormat);
    }
}
