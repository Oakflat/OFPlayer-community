using System.Globalization;
using OFPlayer.WinUI.Core.Transport;

namespace OFPlayer.WinUI.Core.Playback;

/// <summary>Looks up a string-table entry and fills its <c>{placeholders}</c>.</summary>
public delegate string AudioCopy(string key, params (string Name, string Value)[] arguments);

/// <summary>Where the playing audio comes from; decides what the transfer stage can say.</summary>
public enum AudioPlaybackOrigin { None, Local, External, RemoteLibrary, Together }

/// <summary>
/// Everything the signal-path view may use, gathered by the view model from host state.
/// <paramref name="TransportEncoding"/> is the negotiated encoding of the session that
/// actually feeds this playback, never a requested quality.
/// </summary>
public sealed record AudioPathInput(
    bool Connected,
    AudioSignalPath? SignalPath,
    AudioPlaybackOrigin Origin,
    string? TransportEncoding = null,
    string? SourceFormat = null,
    long SourceFileSampleRate = 0,
    long SourceFileBitDepth = 0,
    string? ActiveEngine = null);

public sealed record AudioPathStage(string Label, string Value);

/// <summary>
/// The audio signal path: a one-line summary, the four stages (source, transfer, decoded
/// PCM, output stream) and the known transformations. Unknown stays unknown.
/// </summary>
public sealed record AudioPathView(bool Available, string Summary, IReadOnlyList<AudioPathStage> Stages, IReadOnlyList<string> Facts);

public sealed record AudioEngineView(string Status, string? Notice, bool Selectable, string? Help);

public sealed record AudioDeviceOption(string Id, string Name, bool IsDefault);

public sealed record AudioDeviceChoice(string? Id, string Label);

public sealed record AudioDeviceView(IReadOnlyList<AudioDeviceChoice> Choices, int SelectedIndex, bool Selectable, string? Note);

/// <summary>
/// Copy rules for Settings › Playback › Audio output. The host owns engine and device
/// preferences, the active output and the signal path; this only words them. A
/// preference is never presented as an open stream, and nothing here claims a verified
/// bit-perfect, exclusive remote or gapless path.
/// </summary>
public static class AudioOutputPresentation
{
    public const string ExclusiveFallbackPrefix = "wasapi-exclusive-fallback:";

    public static string EngineKey(string id) => id switch
    {
        "xaudio2" => "settings.playback.audioEngine.xaudio2",
        "wasapi-exclusive" => "settings.playback.audioEngine.exclusive",
        _ => "settings.playback.audioEngine.default",
    };

    /// <summary>
    /// Plain reason for a refused or unconfirmed engine/device change. The host's own
    /// message only selects the reason; it is never shown.
    /// </summary>
    public static string FailureKey(Exception error) => error switch
    {
        HostRequestTimeoutException => "audio.engine.error.unknownOutcome",
        _ when error.Message == "together-audio-engine-change-requires-release" => "audio.engine.error.together",
        _ when error.Message == "remote-audio-engine-change-requires-release" => "audio.engine.error.remote",
        _ when error.Message == "wasapi-exclusive-local-only" => "audio.engine.error.localOnly",
        _ when error.Message.Contains("system default output device", StringComparison.Ordinal) => "audio.device.defaultOnly",
        _ => "audio.engine.error.generic",
    };

    /// <summary>
    /// The engine card's status. <paramref name="togetherOwned"/> and
    /// <paramref name="remotePlayback"/> come from the playback snapshot: while either holds
    /// the host refuses engine changes, so the choice is locked with the reason instead.
    /// </summary>
    public static AudioEngineView Engine(AudioEngineStatus? status, bool connected, bool togetherOwned, bool remotePlayback,
        string? requestFailureKey, AudioCopy text)
    {
        if (!connected) return new(text("audio.path.unavailable"), null, false, null);
        if (status is null) return new(text("settings.playback.audioEngine.loading"), requestFailureKey is null ? null : text(requestFailureKey), false, null);
        var active = status.ActiveEngine is { } activeId
            ? text("settings.playback.audioEngine.active", ("name", text(EngineKey(activeId))))
            : text("settings.playback.audioEngine.noOutput");
        string? notice = null;
        if (status.Error is { Length: > 0 } error)
            notice = error.StartsWith(ExclusiveFallbackPrefix, StringComparison.Ordinal) ? text("settings.playback.audioEngine.exclusiveFallback")
                : error == "wasapi-exclusive-local-only" ? text("audio.engine.error.localOnly")
                : text("audio.engine.error.output");
        else if (status.PreferredEngine == "wasapi-exclusive" && status.ActiveEngine is { } shared && shared != "wasapi-exclusive")
            notice = text("audio.engine.exclusiveNotActive", ("name", text(EngineKey(shared))));
        string? help = null;
        if (togetherOwned) help = text("audio.engine.together");
        else if (remotePlayback) help = text("audio.engine.error.remote");
        else if (status.PreferredEngine == "wasapi-exclusive") help = text("settings.playback.audioEngine.exclusiveHint");
        if (requestFailureKey is not null) notice = text(requestFailureKey);
        return new(active, notice, !togetherOwned && !remotePlayback, help);
    }

    /// <summary>
    /// Output device choices for the preferred engine. XAudio2 and exclusive output use only
    /// the system default endpoint, so they get no picker of unrelated endpoints.
    /// </summary>
    public static AudioDeviceView Devices(string? preferredEngine, IReadOnlyList<AudioDeviceOption> devices, string? preferredDeviceId,
        bool prefersSystemDefault, bool preferredDeviceAvailable, string? activeDeviceName, AudioCopy text)
    {
        var system = new AudioDeviceChoice(null, text("player.output.system"));
        if (preferredEngine is "xaudio2" or "wasapi-exclusive")
            return new([system], 0, false, text("audio.device.defaultOnly"));
        var choices = new List<AudioDeviceChoice> { system };
        foreach (var device in devices)
        {
            // Default-only engines list a virtual endpoint for status, not a choice.
            if (device.Id.Length == 0 || device.Id.EndsWith(":default", StringComparison.Ordinal)) continue;
            choices.Add(new(device.Id, device.IsDefault ? text("player.output.default", ("name", device.Name)) : device.Name));
        }
        if (prefersSystemDefault || preferredDeviceId is null) return new(choices, 0, true, null);
        var index = choices.FindIndex(choice => choice.Id == preferredDeviceId);
        if (index >= 0 && preferredDeviceAvailable) return new(choices, index, true, null);
        // A saved device that is not connected stays the preference; say what plays instead.
        if (index < 0)
        {
            choices.Add(new(preferredDeviceId, text("audio.device.savedUnavailable")));
            index = choices.Count - 1;
        }
        var fallback = string.IsNullOrWhiteSpace(activeDeviceName) ? text("player.output.system") : activeDeviceName;
        return new(choices, index, true, text("audio.device.preferredMissing", ("device", fallback)));
    }

    public static AudioPathView Path(AudioPathInput input, CultureInfo culture, AudioCopy text)
    {
        var unknown = text("audio.path.unknown");
        if (!input.Connected || input.Origin == AudioPlaybackOrigin.None || input.SignalPath is not { } path)
            return new(false, text("audio.path.unavailable"), [], []);

        string Rate(uint rate) => text("audio.path.rate", ("rate", (rate / 1000.0).ToString("0.###", culture)));
        string Channels(uint channels) => channels switch
        {
            1 => text("audio.path.channels.mono"),
            2 => text("audio.path.channels.stereo"),
            _ => text("audio.path.channels.count", ("count", channels.ToString(culture))),
        };
        string? Sample(AudioSignalFormat format) => format.SampleFormat switch
        {
            "f32" => text("audio.path.sample.float", ("bits", "32")),
            "f64" => text("audio.path.sample.float", ("bits", "64")),
            _ when format.BitDepth is uint bits && format.SampleFormat is { Length: > 1 } name && name[0] is 'i' or 'u' =>
                text("audio.path.sample.integer", ("bits", bits.ToString(culture))),
            _ => null,
        };
        string Join(params string?[] parts) => string.Join(text("audio.path.separator"), parts.Where(part => !string.IsNullOrEmpty(part)));

        var original = input.TransportEncoding == "original-file-bytes";
        var streamed = input.Origin is AudioPlaybackOrigin.Together or AudioPlaybackOrigin.RemoteLibrary;

        // Source: the file's own metadata, or what the session's original file is named.
        var format = string.IsNullOrWhiteSpace(input.SourceFormat) ? null : input.SourceFormat.Trim();
        var fileRate = input.SourceFileSampleRate is > 0 and <= uint.MaxValue ? Rate((uint)input.SourceFileSampleRate) : null;
        long depth = input.SourceFileBitDepth > 0 ? input.SourceFileBitDepth : path.Source.BitDepth ?? 0;
        var fileDepth = depth > 0 ? text("audio.path.sample.integer", ("bits", depth.ToString(culture))) : null;
        var source = Join(format, fileRate, fileDepth);

        var transport = input.Origin switch
        {
            AudioPlaybackOrigin.Local => text("audio.path.transport.local"),
            _ when streamed && original => text("audio.path.transport.original"),
            _ when streamed && input.TransportEncoding == "opus-vbr" => text("audio.path.transport.compressed"),
            _ => unknown,
        };

        // A DSD source can report its own 1-bit rate; the PCM rate it becomes is not reported.
        var dsd = path.Source.SampleFormat?.StartsWith("dsd", StringComparison.Ordinal) == true;
        var decoded = Join(dsd ? null : Rate(path.Source.SampleRate), Channels(path.Source.Channels));
        var outputSample = Sample(path.Output);
        var output = Join(Rate(path.Output.SampleRate), Channels(path.Output.Channels), outputSample);
        var stages = new List<AudioPathStage>
        {
            new(text("audio.path.stage.source"), source.Length == 0 ? unknown : source),
            new(text("audio.path.stage.transport"), transport),
            new(text("audio.path.stage.decoded"), decoded),
            new(text("audio.path.stage.output"), input.ActiveEngine is { } engine ? Join(output, text(EngineKey(engine))) : output),
        };

        var facts = new List<string>();
        if (streamed && original) facts.Add(text("audio.path.originalNote"));
        // Together Original decodes at the file's own rate; Opus always decodes at 48 kHz.
        if (input.Origin == AudioPlaybackOrigin.Together && original) facts.Add(text("audio.path.nativeRate"));
        if (dsd) facts.Add(text("audio.path.dsd"));
        if (path.Resampled) facts.Add(text("audio.path.resampled", ("source", Rate(path.Source.SampleRate)), ("output", Rate(path.Output.SampleRate))));
        if (path.ChannelConverted) facts.Add(text("audio.path.channelConverted", ("source", Channels(path.Source.Channels)), ("output", Channels(path.Output.Channels))));
        if (path.SoftwareVolume) facts.Add(text("audio.path.softwareVolume"));
        if (path.SoftwareMixer) facts.Add(text("audio.path.softwareMixer"));
        // The host reports no completed bit-exact measurement; never promote a flag to a badge.
        facts.Add(text("audio.path.bitPerfectUnverified"));

        var summary = new List<string>
        {
            text("audio.path.summary",
                ("decoded", decoded),
                ("output", Join(Rate(path.Output.SampleRate), outputSample))),
        };
        if (path.Resampled) summary.Add(text("audio.path.resampledShort"));
        summary.Add(text("audio.path.bitPerfectUnverified"));
        return new(true, string.Join(" · ", summary), stages, facts);
    }

    /// <summary>Engineering-only line for the exclusive render loop.</summary>
    public static string? Diagnostics(AudioExclusiveDiagnostics? diagnostics, CultureInfo culture, AudioCopy text) => diagnostics is null ? null
        : text("audio.engine.diagnostics",
            ("scheduling", diagnostics.Scheduling),
            ("rate", diagnostics.SampleRate.ToString(culture)),
            ("frames", diagnostics.BufferFrames.ToString(culture)),
            ("empty", diagnostics.EmptyBufferEvents.ToString(culture)),
            ("starved", diagnostics.DecoderStarvations.ToString(culture)),
            ("gap", diagnostics.MaxServiceGapUs.ToString(culture)),
            ("packets", diagnostics.PacketsWritten.ToString(culture)));

    /// <summary>Format name for a session's original file, from its verified MIME.</summary>
    public static string? SessionFormat(string? encoding, string? mime) =>
        encoding == "original-file-bytes" && ConnectTransportPresentation.Format(mime) is { Length: > 0 } name ? name : null;
}
