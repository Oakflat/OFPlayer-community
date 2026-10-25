namespace OFPlayer.WinUI.Core.Input;

public readonly record struct PickerBounds(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsValid => Width > 0 && Height > 0;
}

/// <summary>Physical-pixel bounds for a newly opened desktop file picker.</summary>
public static class FilePickerPlacement
{
    public static PickerBounds? Fit(PickerBounds current, PickerBounds owner, PickerBounds workArea,
        uint dpi, bool maximized)
    {
        if (!current.IsValid || !owner.IsValid || !workArea.IsValid) return null;
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var widthLimit = Math.Max(1, (int)Math.Min(960 * scale, workArea.Width * 0.85));
        var heightLimit = Math.Max(1, (int)Math.Min(640 * scale, workArea.Height * 0.85));
        var fits = current.Width <= widthLimit && current.Height <= heightLimit
            && current.X >= workArea.X && current.Y >= workArea.Y
            && current.Right <= workArea.Right && current.Bottom <= workArea.Bottom;
        // Keep a reasonable remembered size and position. Only recover an oversized,
        // maximized or off-screen initial window, not subsequent user resizing.
        if (fits && !maximized) return null;
        var width = Math.Min(current.Width, widthLimit);
        var height = Math.Min(current.Height, heightLimit);
        var x = Math.Clamp(owner.X + (owner.Width - width) / 2, workArea.X, workArea.Right - width);
        var y = Math.Clamp(owner.Y + (owner.Height - height) / 2, workArea.Y, workArea.Bottom - height);
        return new PickerBounds(x, y, width, height);
    }
}
