using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using OFPlayer.WinUI.Core.Appearance;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OFPlayer_WinUI.Views;
/// <summary>
/// Turns encoded picture bytes into the small, softened bitmap the content backdrop
/// stretches over the pages. The decode is scaled down first, so the blur stays cheap.
/// </summary>
internal static class BackdropBitmap
{
    public static async Task<WriteableBitmap?> CreateAsync(byte[] bytes, int maxWidth, int radius, CancellationToken cancellation)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        cancellation.ThrowIfCancellationRequested();
        // Scaling happens before the photo's own rotation is applied, so a rotated photo is
        // scaled with its sides swapped.
        var(width, height) = BackdropPixels.Fit((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight, maxWidth);
        if (width == 0)
            return null;
        var rotated = decoder.OrientedPixelWidth != decoder.PixelWidth;
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)(rotated ? height : width),
            ScaledHeight = (uint)(rotated ? width : height),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var pixels = data.DetachPixelData();
        cancellation.ThrowIfCancellationRequested();
        if (radius > 0)
            await Task.Run(() => BackdropPixels.BoxBlur(pixels, width, height, radius), cancellation);
        var bitmap = new WriteableBitmap(width, height);
        using (var target = bitmap.PixelBuffer.AsStream())
            await target.WriteAsync(pixels, cancellation);
        bitmap.Invalidate();
        return bitmap;
    }
}
