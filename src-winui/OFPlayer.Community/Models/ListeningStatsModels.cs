using System.Text.Json;

namespace OFPlayer_WinUI.Models;
/// <summary>R3 UI17: full listening-stats snapshot (old ListeningStatsPanel data).</summary>
public sealed record ListeningStatsSummary(double TotalSeconds, int PlayCount, int TrackCount, int AlbumCount, int ActiveDays, string? PeakDay, double PeakDaySeconds, int LongestStreakDays);
public sealed record ListeningStatsDay(string Date, double Seconds, int PlayCount);
public sealed record ListeningStatsTrackRow(string Title, string Artist, double ListenSeconds, int PlayCount, string Artwork = "");
public sealed record ListeningStatsAlbumRow(string Album, string AlbumArtist, double ListenSeconds, int PlayCount, int TrackCount, string Artwork = "");
public sealed record ListeningStatsData(ListeningStatsSummary Summary, IReadOnlyList<ListeningStatsDay> Daily, IReadOnlyList<ListeningStatsTrackRow> TopTracks, IReadOnlyList<ListeningStatsAlbumRow> AlbumGroups)
{
    public static ListeningStatsData? Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("summary", out var summaryElement) || summaryElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var summary = new ListeningStatsSummary(ReadNumber(result, "summary", "totalSeconds"), ReadInt(result, "summary", "playCount"), ReadInt(result, "summary", "trackCount"), ReadInt(result, "summary", "albumCount"), ReadInt(result, "summary", "activeDays"), ReadText(result, "summary", "peakDay"), ReadNumber(result, "summary", "peakDaySeconds"), ReadInt(result, "summary", "longestStreakDays"));
        var daily = new List<ListeningStatsDay>();
        if (result.TryGetProperty("daily", out var dailyArray) && dailyArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in dailyArray.EnumerateArray())
            {
                var date = ReadText(entry, "date");
                if (!string.IsNullOrWhiteSpace(date))
                {
                    daily.Add(new ListeningStatsDay(date, ReadNumber(entry, "seconds"), ReadInt(entry, "playCount")));
                }
            }
        }

        var topTracks = new List<ListeningStatsTrackRow>();
        if (result.TryGetProperty("topTracks", out var trackArray) && trackArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in trackArray.EnumerateArray())
            {
                topTracks.Add(new ListeningStatsTrackRow(ReadText(entry, "title") ?? Services.Localization.Get("track.untitled"), ReadText(entry, "artist") ?? string.Empty, ReadNumber(entry, "listenSeconds"), ReadInt(entry, "playCount"), ReadText(entry, "artwork") ?? string.Empty));
            }
        }

        var albumGroups = new List<ListeningStatsAlbumRow>();
        if (result.TryGetProperty("albumGroups", out var albumArray) && albumArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in albumArray.EnumerateArray())
            {
                albumGroups.Add(new ListeningStatsAlbumRow(ReadText(entry, "album") ?? Services.Localization.Get("track.unknownAlbum"), ReadText(entry, "albumArtist") ?? string.Empty, ReadNumber(entry, "listenSeconds"), ReadInt(entry, "playCount"), ReadInt(entry, "trackCount"), ReadText(entry, "artwork") ?? string.Empty));
            }
        }

        return new ListeningStatsData(summary, daily, topTracks, albumGroups);
    }

    private static string? ReadText(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? ReadText(JsonElement element, string section, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(section, out var inner) ? ReadText(inner, field) : null;
    private static double ReadNumber(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
    private static double ReadNumber(JsonElement element, string section, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(section, out var inner) ? ReadNumber(inner, field) : 0;
    private static int ReadInt(JsonElement element, string field) => (int)Math.Round(ReadNumber(element, field));
    private static int ReadInt(JsonElement element, string section, string field) => (int)Math.Round(ReadNumber(element, section, field));
}
