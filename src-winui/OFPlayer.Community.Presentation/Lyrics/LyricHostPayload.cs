using System.Text.Json;

namespace OFPlayer.WinUI.Core.Lyrics;

/// <summary>Maps the native lyrics JSON once for both the native panel and capsule.</summary>
public static class LyricHostPayload
{
    public static IReadOnlyList<LyricTimeline.InputLine> ParseLines(JsonElement lines)
    {
        var result = new List<LyricTimeline.InputLine>();
        if (lines.ValueKind != JsonValueKind.Array) return result;

        foreach (var line in lines.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.Object) continue;
            var words = new List<LyricTimeline.Word>();
            if (line.TryGetProperty("words", out var wordArray) && wordArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var word in wordArray.EnumerateArray())
                {
                    if (word.ValueKind != JsonValueKind.Object) continue;
                    words.Add(new LyricTimeline.Word(
                        ReadString(word, "text") ?? string.Empty,
                        ReadNumber(word, "startTime"),
                        ReadNumber(word, "endTime")));
                }
            }

            result.Add(new LyricTimeline.InputLine(
                ReadInt(line, "index") ?? result.Count,
                ReadString(line, "text") ?? string.Empty,
                ReadNumber(line, "startTime"),
                ReadNumber(line, "endTime"),
                ReadString(line, "translatedLyric") ?? string.Empty,
                ReadString(line, "romanLyric") ?? string.Empty)
            {
                Words = words,
                IsBackground = ReadBool(line, "isBG"),
                IsDuet = ReadBool(line, "isDuet"),
                Agent = ReadString(line, "agent"),
            });
        }

        return result;
    }

    private static string? ReadString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static double? ReadNumber(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number : null;

    private static int? ReadInt(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : null;

    private static bool ReadBool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
