using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;

namespace OFPlayer.WindowsMedia;

// Independent local-file thumbnail decoding. This exports the source frame,
// not the compositor surface, so subtitles/zoom/pan are intentionally absent.
public static class WindowsVideoFrameCapture
{
    public static async Task<byte[]> PngAsync(string path, double seconds, int width, int height, CancellationToken cancellation)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new FileNotFoundException("截图源文件不存在。", path);
        if (!double.IsFinite(seconds) || seconds < 0 || width <= 0 || height <= 0)
            throw new ArgumentException("截图位置或画面尺寸无效。");
        var sourceInfo = new FileInfo(path);
        var sourceLength = sourceInfo.Length;
        var sourceTime = sourceInfo.LastWriteTimeUtc;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        try {
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(token);
        var clip = await MediaClip.CreateFromFileAsync(file).AsTask(token);
        var duration = clip.OriginalDuration.TotalSeconds;
        if (!double.IsFinite(duration) || duration <= 0) throw new NotSupportedException("Windows 无法读取该视频的截图时长。");
        var scale = Math.Min(1, Math.Min(3840d / width, 2160d / height));
        var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(height * scale));
        var position = TimeSpan.FromSeconds(Math.Min(seconds, Math.Max(0, duration - 0.001)));
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        using var thumbnail = await composition.GetThumbnailAsync(position, targetWidth, targetHeight, VideoFramePrecision.NearestFrame).AsTask(token);
        var decoder = await BitmapDecoder.CreateAsync(thumbnail).AsTask(token);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(token);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask(token);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(token);
        token.ThrowIfCancellationRequested();
        if (output.Size == 0 || output.Size > 64 * 1024 * 1024) throw new IOException("截图大小超出限制。");
        output.Seek(0);
        using var source = output.AsStreamForRead();
        var bytes = new byte[checked((int)output.Size)];
        await source.ReadExactlyAsync(bytes, token);
        sourceInfo.Refresh();
        if (!sourceInfo.Exists || sourceInfo.Length != sourceLength || sourceInfo.LastWriteTimeUtc != sourceTime)
            throw new IOException("截图期间源视频已更改，请重试。");
        return bytes;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException("Windows 截图解码超时。"); }
    }
}
