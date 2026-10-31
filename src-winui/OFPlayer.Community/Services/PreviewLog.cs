using System.Threading.Channels;
using OFPlayer.WinUI.Core.Diagnostics;

namespace OFPlayer_WinUI.Services;
/// <summary>
/// Minimal preview diagnostics: appends lifecycle and host-connection messages to
/// %TEMP%\ofplayer-community-preview.log. Deliberately outside the data root so preview
/// logging never mixes with catalog state, and never contains tokens or library
/// contents (only paths and error messages).
/// </summary>
internal static class PreviewLog
{
    public static string LogPath { get; } = Path.Combine(Path.GetTempPath(), "ofplayer-community-preview.log");

    private static readonly Channel<string> Queue = Channel.CreateBounded<string>(new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private static readonly Task Writer = StartWriter();
    private static long dropped;
    private static Task StartWriter()
    {
        using (ExecutionContext.SuppressFlow())
            return Task.Run(WriteLoopAsync);
    }

    public static void Write(string message)
    {
        var started = PerformanceTrace.NowMs;
        // Preserve the caller's timestamp, but never wait for the filesystem or
        // a logging lock while dispatching input, WebView or playback events.
        if (!Queue.Writer.TryWrite($"{DateTime.Now:HH:mm:ss.fff} {message}"))
            Interlocked.Increment(ref dropped);
        PerformanceTrace.Activity("diagnostics.previewLog.enqueue", started);
    }

    private static async Task WriteLoopAsync()
    {
        while (await Queue.Reader.WaitToReadAsync())
        {
            var batch = new List<string>(256);
            while (batch.Count < 256 && Queue.Reader.TryRead(out var line))
                batch.Add(line);
            var started = PerformanceTrace.NowMs;
            try
            {
                await File.AppendAllLinesAsync(LogPath, batch).ConfigureAwait(false);
            }
            catch (IOException)
            {
                Interlocked.Add(ref dropped, batch.Count);
            }
            catch (UnauthorizedAccessException)
            {
                Interlocked.Add(ref dropped, batch.Count);
            }
            finally
            {
                PerformanceTrace.Activity("diagnostics.previewLog.backgroundWrite", started, "io");
                PerformanceTrace.Sample("diagnostics.previewLog.dropped", Interlocked.Read(ref dropped));
            }

            if (!Queue.Reader.Completion.IsCompleted)
                await Task.Delay(50).ConfigureAwait(false);
        }
    }

    public static async Task FlushAndStopAsync()
    {
        Queue.Writer.TryComplete();
        await Writer.ConfigureAwait(false);
    }
}
