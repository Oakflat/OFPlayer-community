using System.Text.Json;
using System.Text.Json.Nodes;

namespace OFPlayer.WinUI.Core.Appearance;

/// <summary>
/// What sits behind the content area (the pages to the right of the sidebar). The sidebar
/// and title bar keep the system's frosted glass whatever is chosen here. Stored whole as
/// the <c>contentBackdrop</c> preference; anything missing or unrecognised falls back field
/// by field, so an older or hand-edited profile still yields a complete value.
/// </summary>
/// <param name="Kind"><c>plain</c> (today's solid surface), <c>cover</c> (a soft wash made
/// from the playing song's artwork) or <c>image</c> (a picture of the listener's own).</param>
/// <param name="Strength">How much of the backdrop shows through the page surfaces, in percent.</param>
/// <param name="Blur">Blur of an own picture; the artwork wash is always fully soft.</param>
/// <param name="ImageFile">File name of the copied picture inside the app's appearance folder.</param>
/// <param name="BarClarity">How clearly the songs show through the playback bar's frosted glass, in percent.</param>
public sealed record ContentBackdrop(string Kind, int Strength, int Blur, string ImageFile, int BarClarity)
{
    public const string PreferenceKey = "contentBackdrop";
    public const string Plain = "plain";
    public const string Cover = "cover";
    public const string Image = "image";
    public const int MinStrength = 10;
    /// <summary>At full strength the page surfaces are clear and the backdrop shows as it is.</summary>
    public const int MaxStrength = 100;
    public const int MaxBlur = 40;
    public const int MaxBarClarity = 100;

    public static readonly string[] Kinds = { Plain, Cover, Image };

    public static ContentBackdrop Default { get; } = new(Plain, 35, 16, "", 60);

    public static ContentBackdrop FromPreferences(JsonElement preferences) =>
        preferences.ValueKind == JsonValueKind.Object && preferences.TryGetProperty(PreferenceKey, out var value)
            ? FromJson(value)
            : Default;

    public static ContentBackdrop FromJson(JsonElement value)
    {
        var fallback = Default;
        if (value.ValueKind != JsonValueKind.Object) return fallback;

        int Number(string field, int current) =>
            value.TryGetProperty(field, out var element) && element.ValueKind == JsonValueKind.Number &&
            element.TryGetDouble(out var number) && double.IsFinite(number)
                ? (int)Math.Round(number)
                : current;

        var kind = value.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String &&
            Array.IndexOf(Kinds, kindElement.GetString()) >= 0
                ? kindElement.GetString()!
                : fallback.Kind;
        var file = value.TryGetProperty("imageFile", out var fileElement) && fileElement.ValueKind == JsonValueKind.String
            ? fileElement.GetString() ?? ""
            : "";
        return new ContentBackdrop(kind, Number("strength", fallback.Strength), Number("blur", fallback.Blur), file,
            Number("barClarity", fallback.BarClarity)).Normalize();
    }

    public ContentBackdrop Normalize() => this with
    {
        Kind = Array.IndexOf(Kinds, Kind) >= 0 ? Kind : Default.Kind,
        Strength = Math.Clamp(Strength, MinStrength, MaxStrength),
        Blur = Math.Clamp(Blur, 0, MaxBlur),
        ImageFile = IsSafeFileName(ImageFile) ? ImageFile : "",
        BarClarity = Math.Clamp(BarClarity, 0, MaxBarClarity),
    };

    public JsonObject ToJson() => new()
    {
        ["kind"] = Kind,
        ["strength"] = Strength,
        ["blur"] = Blur,
        ["imageFile"] = ImageFile,
        ["barClarity"] = BarClarity,
    };

    /// <summary>Only an own picture is a Pro look; the plain surface and the artwork wash are for everyone.</summary>
    public bool RequiresPro => Kind == Image;

    /// <summary>
    /// What the window actually shows. An own picture without Pro (or Hub) becomes the artwork
    /// wash, and one whose file is gone becomes the plain surface; the saved choice and the
    /// picture itself are kept, so renewing brings them back.
    /// </summary>
    public ContentBackdrop Effective(bool canUseProFeatures)
    {
        if (Kind != Image) return this;
        if (ImageFile.Length == 0) return this with { Kind = Plain };
        return canUseProFeatures ? this : this with { Kind = Cover };
    }

    /// <summary>
    /// How opaque the page surfaces stay over this backdrop: fully opaque on the plain
    /// surface, otherwise as much as the strength leaves.
    /// </summary>
    public double SurfaceOpacity => Kind == Plain ? 1 : 1 - Strength / 100.0;

    /// <summary>
    /// The playback bar's frost at this clarity: how opaque its tint and luminosity layers
    /// are. Full clarity still keeps a thin tint, so the controls stay readable over any list.
    /// </summary>
    public (double Tint, double Luminosity) BarFrost
    {
        get
        {
            var clarity = Math.Clamp(BarClarity, 0, MaxBarClarity) / 100.0;
            return (Math.Round(0.80 - 0.70 * clarity, 3), Math.Round(0.90 - 0.60 * clarity, 3));
        }
    }

    /// <summary>
    /// A choice from Settings. Without Pro an own picture is only previewed: it shows until
    /// the listener leaves Settings, and the saved look stays as it was. The bar's frost is
    /// for everyone, so it is saved even while a picture is only previewed.
    /// </summary>
    public static (ContentBackdrop Saved, ContentBackdrop? Preview) Choose(
        ContentBackdrop saved, ContentBackdrop requested, bool canUseProFeatures)
    {
        requested = requested.Normalize();
        return requested.RequiresPro && !canUseProFeatures
            ? (saved with { BarClarity = requested.BarClarity }, requested)
            : (requested, null);
    }

    /// <summary>A bare file name in the appearance folder: no directories, no parent references.</summary>
    public static bool IsSafeFileName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 120 && name == Path.GetFileName(name) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name != "." && name != "..";
}
