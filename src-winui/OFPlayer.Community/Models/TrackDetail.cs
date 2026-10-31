using System.Text.Json;

namespace OFPlayer_WinUI.Models;
/// <summary>
/// N2 detail panel: full metadata for one track (`desktop_catalog_get_track`).
/// The Rust host owns the row; this record only decodes it for display.
/// </summary>
public sealed record TrackDetail(string Id, string Title, string Artist, string Album, double DurationSeconds, string Format, long Bitrate, long SampleRate, long BitDepth, string FilePath, string? Artwork, string? OriginalFilename = null, bool DurationEstimated = false, bool BitrateEstimated = false, string AlbumArtist = "", string Genre = "", string Date = "", string Track = "", long Channels = 0)
{
    public string DurationText => DurationSeconds > 0 ? (DurationEstimated ? "≈" : "") + TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "—";
    public string BitrateText => Bitrate > 0 ? $"{(BitrateEstimated ? "≈" : "")}{Bitrate / 1000} kbps" : "—";
    public string SampleRateText => SampleRate > 0 ? (SampleRate / 1000.0).ToString("0.#", Services.Localization.Culture) + " kHz" : "—";
    public string BitDepthText => BitDepth > 0 ? $"{BitDepth} bit" : "—";
    public string FileName => string.IsNullOrEmpty(FilePath) ? "—" : Path.GetFileName(FilePath);
    public bool HasArtwork => !string.IsNullOrWhiteSpace(Artwork);

    public static TrackDetail FromJson(JsonElement element)
    {
        var id = ReadString(element, "id") ?? string.Empty;
        var title = ReadString(element, "displayTitle") ?? ReadString(element, "title") ?? ReadString(element, "fileName") ?? Services.Localization.Get("track.untitled");
        var artist = ReadString(element, "artist") ?? string.Empty;
        var album = ReadString(element, "album") ?? string.Empty;
        var duration = ReadNumber(element, "duration");
        var format = ReadString(element, "format")?.ToUpperInvariant() ?? string.Empty;
        var bitrate = (long)ReadNumber(element, "bitrate");
        var sampleRate = (long)ReadNumber(element, "sampleRate");
        var bitDepth = (long)ReadNumber(element, "bitDepth");
        var filePath = element.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object ? ReadString(source, "path") ?? string.Empty : string.Empty;
        var artwork = ReadString(element, "artwork");
        return new TrackDetail(id, title, artist, album, duration, format, bitrate, sampleRate, bitDepth, filePath, string.IsNullOrWhiteSpace(artwork) ? null : artwork, AlbumArtist: ReadString(element, "albumArtist") ?? "", Genre: ReadString(element, "genre") ?? "", Date: ReadString(element, "date") ?? "", Track: ReadString(element, "track") ?? "", Channels: (long)ReadNumber(element, "channels"));
    }

    private static string? ReadString(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double ReadNumber(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0.0;
}
