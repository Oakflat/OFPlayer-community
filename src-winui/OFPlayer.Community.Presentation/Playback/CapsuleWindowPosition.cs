using System.Text.Json;

namespace OFPlayer.WinUI.Core.Playback;

/// <summary>Decodes WebView2 capsule logical or physical coordinates into native screen pixels.</summary>
public static class CapsuleWindowPosition
{
    public static (int X, int Y) ToPhysical(JsonElement value, double scale)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Capsule position must be an object.");

        var logical = true;
        if (value.TryGetProperty("Logical", out var coordinates)) value = coordinates;
        else if (value.TryGetProperty("Physical", out coordinates))
        {
            value = coordinates;
            logical = false;
        }
        // Keep direct x/y payloads compatible with earlier bridge callers.
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("x", out var x) || x.ValueKind != JsonValueKind.Number
            || !value.TryGetProperty("y", out var y) || y.ValueKind != JsonValueKind.Number
            || !x.TryGetDouble(out var px) || !y.TryGetDouble(out var py))
            throw new ArgumentException("Capsule position requires numeric x and y.");

        var factor = logical ? scale : 1;
        if (!double.IsFinite(factor) || factor <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale));
        return (checked((int)Math.Round(px * factor)), checked((int)Math.Round(py * factor)));
    }
}
