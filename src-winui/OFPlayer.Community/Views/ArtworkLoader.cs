using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;

namespace OFPlayer_WinUI.Views;
/// <summary>D1: shared artwork loader (data URI or local file) used by the shell views.</summary>
internal static class ArtworkLoader
{
    private sealed class RequestState
    {
        public string? Key;
        public int Revision;
        public Task Pending = Task.CompletedTask;
        public CancellationTokenSource? Cancellation;
    }

    private const int MaxArtworkBytes = 32 * 1024 * 1024;
    private static readonly SemaphoreSlim Preparations = new(4);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Image, RequestState> Requests = new();
    // UI-thread-only, bounded decoded thumbnail cache. Returning to a virtualized
    // card can assign its bitmap synchronously instead of showing a blank decode frame.
    private static readonly Dictionary<(string, int, long), BitmapImage> Bitmaps = new();
    private static readonly Queue<(string, int, long)> BitmapOrder = new();
    internal static void TrimCache()
    {
        Bitmaps.Clear();
        BitmapOrder.Clear();
    }

    private static void Remember((string, int, long) key, BitmapImage bitmap)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ArtworkLoader.Remember");
        if (Bitmaps.ContainsKey(key))
            return;
        while (BitmapOrder.Count >= 48)
            Bitmaps.Remove(BitmapOrder.Dequeue());
        Bitmaps[key] = bitmap;
        BitmapOrder.Enqueue(key);
    }

    // Called on the UI thread. Progress ticks reuse the existing source, and
    // recycled rows cannot receive the result of an earlier artwork request.
    public static Task ApplyAsync(Image target, string? artwork)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ArtworkLoader.ApplyAsync");
        var state = Requests.GetOrCreateValue(target);
        var key = string.IsNullOrWhiteSpace(artwork) ? null : artwork;
        if (state.Key == key)
            return state.Pending;
        state.Cancellation?.Cancel();
        state.Cancellation?.Dispose();
        state.Cancellation = new CancellationTokenSource();
        state.Key = key;
        var revision = ++state.Revision;
        target.Source = null;
        state.Pending = LoadAsync(target, key, state, revision, state.Cancellation.Token);
        return state.Pending;
    }

    private static async Task LoadAsync(Image target, string? artwork, RequestState state, int revision, CancellationToken cancellation)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ArtworkLoader.LoadAsync");
        try
        {
            if (string.IsNullOrWhiteSpace(artwork))
            {
                target.Source = null;
                return;
            }

            var width = DecodeWidth(target);
            var isData = artwork.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
            var stamp = isData ? 0 : await Task.Run(() => new FileInfo(artwork).LastWriteTimeUtc.Ticks, cancellation);
            if (cancellation.IsCancellationRequested || state.Revision != revision)
                return;
            var cacheKey = (artwork, width, stamp);
            if (Bitmaps.TryGetValue(cacheKey, out var cached))
            {
                if (state.Revision != revision)
                    return;
                // Already decoded: show it as-is, cancelling any reveal still running.
                if (target.XamlRoot is not null)
                    Motion.ResetOpacity(target);
                target.Source = cached;
                return;
            }

            // Keep the slot until decode completes so prepared byte arrays do not
            // accumulate unboundedly while the UI is busy with many visible rows.
            await Preparations.WaitAsync(cancellation);
            try
            {
                var bytes = isData ? await Task.Run(() => DecodeDataUri(artwork, cancellation), cancellation) : await ReadFileAsync(artwork, cancellation);
                if (cancellation.IsCancellationRequested || state.Revision != revision)
                    return;
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                if (cancellation.IsCancellationRequested || state.Revision != revision)
                    return;
                stream.Seek(0);
                var bitmap = new BitmapImage
                {
                    DecodePixelWidth = width
                };
                await bitmap.SetSourceAsync(stream);
                if (cancellation.IsCancellationRequested || state.Revision != revision)
                    return;
                Remember(cacheKey, bitmap);
                target.Source = bitmap;
                // A cover that arrives after its row is on screen fades in over the
                // placeholder instead of cutting in.
                if (target.XamlRoot is not null)
                    Motion.FadeIn(target);
            }
            finally
            {
                Preparations.Release();
            }
        }
        catch (OperationCanceledException)when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            OFPlayer_WinUI.Services.PreviewLog.Write($"artwork load failed: {error.GetType().Name}: {error.Message}");
            if (state.Revision == revision)
            {
                target.Source = null;
            }
        }
    }

    /// <summary>The encoded bytes behind an artwork reference (data URI or local file), within the same size limit.</summary>
    internal static async Task<byte[]> ReadBytesAsync(string artwork, CancellationToken cancellation) => artwork.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? await Task.Run(() => DecodeDataUri(artwork, cancellation), cancellation) : await ReadFileAsync(artwork, cancellation);
    private static byte[] DecodeDataUri(string uri, CancellationToken cancellation)
    {
        var comma = uri.IndexOf(',');
        if (comma < 0 || !uri[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Artwork data URI must be base64 encoded");
        var encoded = uri.AsSpan(comma + 1);
        var upperBound = ((long)encoded.Length + 3) / 4 * 3;
        if (upperBound > MaxArtworkBytes)
            throw new InvalidDataException("Artwork exceeds the size limit");
        cancellation.ThrowIfCancellationRequested();
        var bytes = new byte[(int)upperBound];
        if (!Convert.TryFromBase64Chars(encoded, bytes, out var written))
            throw new FormatException("Invalid artwork base64");
        if (written != bytes.Length)
            Array.Resize(ref bytes, written);
        return bytes;
    }

    private static async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellation)
    {
        return await Task.Run(async () =>
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = file.Length;
            if (length < 0 || length > MaxArtworkBytes)
                throw new InvalidDataException("Artwork exceeds the size limit");
            var bytes = new byte[(int)length];
            await file.ReadExactlyAsync(bytes, cancellation);
            return bytes;
        }, cancellation);
    }

    private static int DecodeWidth(Image target) => (int)Math.Clamp(Math.Ceiling(Math.Max(target.ActualWidth, 64) * (target.XamlRoot?.RasterizationScale ?? 1)), 128, 1024);
}
