using System.Text.Json;

namespace OFPlayer.WinUI.Core.Playback;

public sealed record AudioEngineOption(string Id, string Name, bool Available);

/// <summary>
/// Render-loop counters of the local exclusive output. Engineering detail only: zero
/// counters do not certify bit-perfect or uninterrupted audible playback.
/// </summary>
public sealed record AudioExclusiveDiagnostics(
    string Scheduling,
    uint SampleRate,
    uint BufferFrames,
    ulong EmptyBufferEvents,
    ulong DecoderStarvations,
    ulong MaxServiceGapUs,
    ulong PacketsWritten)
{
    public static AudioExclusiveDiagnostics? Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        ulong? Count(string name) => value.TryGetProperty(name, out var field) && field.TryGetUInt64(out var number) ? number : null;
        uint? Small(string name) => value.TryGetProperty(name, out var field) && field.TryGetUInt32(out var number) ? number : null;
        var scheduling = value.TryGetProperty("scheduling", out var mode) && mode.ValueKind == JsonValueKind.String ? mode.GetString() : null;
        if (scheduling is null || Small("sampleRate") is not uint rate || Small("bufferFrames") is not uint frames ||
            Count("emptyBufferEvents") is not ulong empty || Count("decoderStarvations") is not ulong starved ||
            Count("maxServiceGapUs") is not ulong gap || Count("packetsWritten") is not ulong packets) return null;
        return new(scheduling, rate, frames, empty, starved, gap, packets);
    }
}

public sealed record AudioEngineStatus(
    string PreferredEngine,
    string? ActiveEngine,
    IReadOnlyList<AudioEngineOption> Engines,
    string? Error)
{
    /// <summary>Windows exclusive render counters; null when that output was never opened.</summary>
    public AudioExclusiveDiagnostics? ExclusiveDiagnostics { get; init; }

    public static AudioEngineStatus Parse(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new FormatException("Audio engine status is not an object.");

        var preferred = RequiredId(value, "preferredEngine");
        string? active = null;
        if (value.TryGetProperty("activeEngine", out var activeValue) && activeValue.ValueKind == JsonValueKind.String)
            active = KnownId(activeValue.GetString());
        else if (value.TryGetProperty("activeEngine", out activeValue) && activeValue.ValueKind != JsonValueKind.Null)
            throw new FormatException("Invalid active audio engine.");

        if (!value.TryGetProperty("engines", out var list) || list.ValueKind != JsonValueKind.Array)
            throw new FormatException("Missing audio engine list.");
        var engines = new List<AudioEngineOption>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("available", out var availableValue) ||
                availableValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new FormatException("Invalid audio engine option.");
            var id = KnownId(idValue.GetString());
            if (engines.Any(existing => existing.Id == id))
                throw new FormatException("Duplicate audio engine option.");
            engines.Add(new AudioEngineOption(id, nameValue.GetString()!, availableValue.GetBoolean()));
        }
        if (!engines.Any(engine => engine.Id == preferred) ||
            (active is not null && !engines.Any(engine => engine.Id == active)))
            throw new FormatException("Audio engine status references an unknown option.");
        string? error = null;
        if (value.TryGetProperty("error", out var errorValue) && errorValue.ValueKind == JsonValueKind.String)
            error = errorValue.GetString();
        var diagnostics = value.TryGetProperty("exclusiveDiagnostics", out var diagnosticsValue)
            ? AudioExclusiveDiagnostics.Parse(diagnosticsValue) : null;
        return new AudioEngineStatus(preferred, active, engines, error) { ExclusiveDiagnostics = diagnostics };
    }

    public bool CanSelect(string id) => Engines.Any(engine => engine.Id == id && engine.Available);

    public static string KnownId(string? value) => value is "default" or "xaudio2" or "wasapi-exclusive"
        ? value : throw new FormatException("Unknown audio engine id.");

    private static string RequiredId(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? KnownId(property.GetString()) : throw new FormatException($"Missing {name}.");
}
