using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace OFPlayer.WindowsMedia;

// Called on the UI thread only after an explicit copy-frame action.
public static class WindowsVideoFrameClipboard
{
    // Flush normally makes Windows own the image. If it fails, keep the one bounded
    // stream alive for delayed clipboard reads until the next successful publication
    // or process exit. Never clear someone else's clipboard to recover from failure.
    private static InMemoryRandomAccessStream? pendingStream;

    public static async Task<bool> CopyPngAsync(byte[] png, Func<bool> isCurrent, CancellationToken cancellation)
    {
        if (png.Length == 0 || png.Length > 64 * 1024 * 1024)
            throw new IOException("截图大小超出限制。");
        InMemoryRandomAccessStream? stream = new();
        try {
            using (var output = stream.GetOutputStreamAt(0))
            using (var writer = new DataWriter(output)) {
                writer.WriteBytes(png);
                await writer.StoreAsync().AsTask(cancellation);
                await writer.FlushAsync().AsTask(cancellation);
                writer.DetachStream();
            }
            stream.Seek(0);
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            cancellation.ThrowIfCancellationRequested();
            if (!isCurrent()) return false;
            // There is no await between the stale guard, publication, and flush.
            Clipboard.SetContent(package);
            var previous = pendingStream;
            pendingStream = stream;
            stream = null; // Ownership transferred, including if Flush throws.
            previous?.Dispose();
            try { Clipboard.Flush(); }
            catch (Exception error) {
                throw new IOException("画面已交给剪贴板，但未能持久保存；关闭播放器后可能无法粘贴，请重试。", error);
            }
            pendingStream.Dispose();
            pendingStream = null;
            return true;
        }
        finally { stream?.Dispose(); }
    }
}
