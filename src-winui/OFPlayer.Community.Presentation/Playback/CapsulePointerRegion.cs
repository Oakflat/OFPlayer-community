namespace OFPlayer.WinUI.Core.Playback;

/// <summary>Input-only capsule geometry, preserving hover through the expansion animation.</summary>
public sealed class CapsulePointerRegion
{
    public const double DefaultCollapsedHeight = 44;
    public const double DefaultExpandedHeight = 74;
    // Window is 112 CSS px tall with the capsule 4 px from the top.
    private const double MaxHeight = 104;
    private bool hovering;

    private static double Height(double value, double fallback) =>
        double.IsFinite(value) && value > 0 ? Math.Clamp(value, 24, MaxHeight) : fallback;

    public bool AcceptsPhysical(double x, double y, double clientWidth, double clientHeight,
        double dpiScale, double capsuleWidth, bool expanded, double viewportWidth = 0, double viewportHeight = 0,
        double collapsedHeight = DefaultCollapsedHeight, double expandedHeight = DefaultExpandedHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0) return false;
        var scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        // WebView CSS pixels can differ from HWND DIPs after zoom or a monitor-scale change.
        var cssWidth = double.IsFinite(viewportWidth) && viewportWidth > 0 ? viewportWidth : clientWidth / scale;
        var cssHeight = double.IsFinite(viewportHeight) && viewportHeight > 0 ? viewportHeight : clientHeight / scale;
        return Accepts(x * cssWidth / clientWidth, y * cssHeight / clientHeight, cssWidth, capsuleWidth, expanded,
            collapsedHeight, expandedHeight);
    }

    public bool Accepts(double x, double y, double canvasWidth, double capsuleWidth, bool expanded,
        double collapsedHeight = DefaultCollapsedHeight, double expandedHeight = DefaultExpandedHeight)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(canvasWidth) || canvasWidth <= 8) return false;
        var width = Math.Min(canvasWidth - 8, double.IsFinite(capsuleWidth) ? Math.Clamp(capsuleWidth, 1, 540) : 540);
        var left = (canvasWidth - width) / 2;
        bool Inside(double height, double cornerRadius)
        {
            const double top = 4;
            var bottom = top + height;
            if (x < left || x >= left + width || y < top || y >= bottom) return false;
            // Match the CSS rounded rectangle, not a stadium whose radius grows with its height.
            var radius = Math.Min(cornerRadius, Math.Min(width, height) / 2);
            var cx = Math.Clamp(x, left + radius, left + width - radius);
            var cy = Math.Clamp(y, top + radius, bottom - radius);
            return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius;
        }
        // The page reports its own heights: they follow font size, the second line and
        // the paused mini form. The collapsed shape is a pill; the expanded one keeps 24px corners.
        var collapsedSize = Height(collapsedHeight, DefaultCollapsedHeight);
        var expandedSize = Math.Max(collapsedSize, Height(expandedHeight, DefaultExpandedHeight));
        var collapsed = Inside(collapsedSize, collapsedSize / 2);
        var extended = Inside(expandedSize, 24);
        hovering = collapsed || (hovering && extended);
        return collapsed || ((expanded || hovering) && extended);
    }
}
