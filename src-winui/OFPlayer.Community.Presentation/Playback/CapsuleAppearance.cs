using System.Text.Json;
using System.Text.Json.Nodes;

namespace OFPlayer.WinUI.Core.Playback;

/// <summary>
/// How the lyric capsule looks. Stored whole as the <c>lyricCapsule</c> preference
/// object and pushed to the capsule page; settings and page read the same values.
/// Anything missing or unrecognised falls back to the default field by field, so an
/// older or hand-edited profile still yields a complete appearance.
/// </summary>
public sealed record CapsuleAppearance(
    string Theme,
    string Accent,
    int Opacity,
    bool Spin,
    string FontSize,
    string SecondLine,
    string Align,
    string Wave,
    string Width,
    string Controls,
    string WhenPaused)
{
    public const string PreferenceKey = "lyricCapsule";
    public const int MinOpacity = 60;
    public const int MaxOpacity = 100;

    public static readonly string[] Themes = { "dark", "light", "cover" };
    public static readonly string[] Accents = { "cover", "blue", "coral", "teal", "amber", "lavender" };
    public static readonly string[] FontSizes = { "small", "medium", "large" };
    public static readonly string[] SecondLines = { "meta", "next", "translation", "none" };
    public static readonly string[] Aligns = { "left", "center" };
    public static readonly string[] Waves = { "echo", "spectrum", "off" };
    /// <summary>"line" follows the lyric being sung; a profile saved with the old "song" falls back to it.</summary>
    public static readonly string[] Widths = { "line", "fixed" };
    public static readonly string[] ControlModes = { "hover", "always" };
    public static readonly string[] PausedModes = { "keep", "mini" };

    public static CapsuleAppearance Default { get; } =
        new("dark", "cover", 100, true, "medium", "meta", "left", "echo", "line", "hover", "keep");

    /// <summary>Reads <c>lyricCapsule</c> from the whole preferences object.</summary>
    public static CapsuleAppearance FromPreferences(JsonElement preferences) =>
        preferences.ValueKind == JsonValueKind.Object && preferences.TryGetProperty(PreferenceKey, out var value)
            ? FromJson(value)
            : Default;

    public static CapsuleAppearance FromJson(JsonElement value)
    {
        var fallback = Default;
        if (value.ValueKind != JsonValueKind.Object) return fallback;

        string Pick(string field, string[] options, string current) =>
            value.TryGetProperty(field, out var element) && element.ValueKind == JsonValueKind.String &&
            Array.IndexOf(options, element.GetString()) >= 0
                ? element.GetString()!
                : current;

        var opacity = value.TryGetProperty("opacity", out var opacityElement) &&
            opacityElement.ValueKind == JsonValueKind.Number &&
            opacityElement.TryGetDouble(out var opacityValue) && double.IsFinite(opacityValue)
                ? ClampOpacity(opacityValue)
                : fallback.Opacity;
        var spin = value.TryGetProperty("spin", out var spinElement) &&
            spinElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? spinElement.GetBoolean()
                : fallback.Spin;

        return new(
            Pick("theme", Themes, fallback.Theme),
            Pick("accent", Accents, fallback.Accent),
            opacity,
            spin,
            Pick("fontSize", FontSizes, fallback.FontSize),
            Pick("secondLine", SecondLines, fallback.SecondLine),
            Pick("align", Aligns, fallback.Align),
            Pick("wave", Waves, fallback.Wave),
            Pick("width", Widths, fallback.Width),
            Pick("controls", ControlModes, fallback.Controls),
            Pick("whenPaused", PausedModes, fallback.WhenPaused));
    }

    /// <summary>The same appearance with every field forced into its allowed values.</summary>
    public CapsuleAppearance Normalize()
    {
        using var document = JsonDocument.Parse(ToJson().ToJsonString());
        return FromJson(document.RootElement);
    }

    public JsonObject ToJson() => new()
    {
        ["theme"] = Theme,
        ["accent"] = Accent,
        ["opacity"] = Opacity,
        ["spin"] = Spin,
        ["fontSize"] = FontSize,
        ["secondLine"] = SecondLine,
        ["align"] = Align,
        ["wave"] = Wave,
        ["width"] = Width,
        ["controls"] = Controls,
        ["whenPaused"] = WhenPaused,
    };

    public static int ClampOpacity(double value) =>
        (int)Math.Round(Math.Clamp(value, MinOpacity, MaxOpacity));
}
