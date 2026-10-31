namespace OFPlayer.WinUI.Core.Appearance;

/// <summary>
/// Pixel work for the content backdrop: how small to decode a picture and how to soften it.
/// The window then stretches the small, blurred bitmap over the content area, which keeps
/// the blur cheap without a graphics dependency.
/// </summary>
public static class BackdropPixels
{
    /// <summary>Artwork for the wash: tiny and fully soft, so only its colours remain.</summary>
    public const int CoverWidth = 48;
    public const int CoverRadius = 6;
    /// <summary>A sharp own picture is decoded close to screen size; a blurred one much smaller.</summary>
    public const int SharpWidth = 2560;
    public const int BlurredWidth = 640;

    /// <summary>Decode width and blur radius (in decoded pixels) for an own picture.</summary>
    public static (int Width, int Radius) ForImage(int blur) =>
        blur <= 0 ? (SharpWidth, 0) : (BlurredWidth, Math.Max(1, (int)Math.Round(Math.Clamp(blur, 0, ContentBackdrop.MaxBlur) * 0.5)));

    /// <summary>Scales <paramref name="width"/> × <paramref name="height"/> down to at most
    /// <paramref name="maxWidth"/> wide, keeping the aspect ratio; never scales up.</summary>
    public static (int Width, int Height) Fit(int width, int height, int maxWidth)
    {
        if (width <= 0 || height <= 0) return (0, 0);
        if (maxWidth <= 0 || width <= maxWidth) return (width, height);
        return (maxWidth, Math.Max(1, (int)Math.Round(height * (double)maxWidth / width)));
    }

    /// <summary>
    /// Three box-blur passes over BGRA pixels, horizontally then vertically, which comes close
    /// to a Gaussian. Edges repeat their outermost pixel, so the borders do not darken.
    /// </summary>
    public static void BoxBlur(byte[] bgra, int width, int height, int radius, int passes = 3)
    {
        if (radius <= 0 || width <= 0 || height <= 0) return;
        if (bgra.Length < width * height * 4) throw new ArgumentException("Pixel buffer is smaller than the image.", nameof(bgra));
        var scratch = new byte[width * height * 4];
        for (var pass = 0; pass < passes; pass++)
        {
            Blur(bgra, scratch, width, height, radius, horizontal: true);
            Blur(scratch, bgra, width, height, radius, horizontal: false);
        }
    }

    private static void Blur(byte[] source, byte[] target, int width, int height, int radius, bool horizontal)
    {
        var lines = horizontal ? height : width;
        var length = horizontal ? width : height;
        var window = radius * 2 + 1;
        Span<int> sum = stackalloc int[4];
        for (var line = 0; line < lines; line++)
        {
            int Index(int position)
            {
                position = Math.Clamp(position, 0, length - 1);
                return (horizontal ? line * width + position : position * width + line) * 4;
            }

            sum.Clear();
            for (var offset = -radius; offset <= radius; offset++)
            {
                var index = Index(offset);
                for (var channel = 0; channel < 4; channel++) sum[channel] += source[index + channel];
            }
            for (var position = 0; position < length; position++)
            {
                var target0 = Index(position);
                for (var channel = 0; channel < 4; channel++) target[target0 + channel] = (byte)((sum[channel] + window / 2) / window);
                var leaving = Index(position - radius);
                var entering = Index(position + radius + 1);
                for (var channel = 0; channel < 4; channel++) sum[channel] += source[entering + channel] - source[leaving + channel];
            }
        }
    }
}
