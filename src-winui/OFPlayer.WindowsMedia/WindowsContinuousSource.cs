using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace OFPlayer.WindowsMedia;

// Owns system demux/audio PCM preparation. MediaPlayer retains video decoding and the A/V clock.
internal sealed class WindowsContinuousSource : IDisposable
{
    private readonly SourceHandle handle;
    private long diagnosticsAt;
    private double diagnosticsPosition, diagnosticsRate;
    private SourceStatistics previous;
    public double TimeScale { get; private set; } = 1;
    public double PlaybackRate { get; private set; } = 1;
    public double Duration { get; }
    public MediaSource Source { get; }
    private WindowsContinuousSource(MediaSource source, SourceHandle handle)
    {
        Source = source; this.handle = handle;
        if (ReadStatistics(handle, out var stats) != 0) throw new InvalidOperationException("无法读取本地视频时长。");
        Duration = stats.Duration / 10000000.0;
    }

    public double SetRate(double rate)
    {
        Marshal.ThrowExceptionForHR(SetRateNative(handle, rate));
        TimeScale = Math.Max(1, rate / 3);
        PlaybackRate = rate;
        diagnosticsAt = 0;
        return rate / TimeScale;
    }
    public double MoviePosition(double position) => position * TimeScale;
    public double MediaPosition(double position) => position / TimeScale;

    public static async Task<WindowsContinuousSource?> TryOpenAsync(string path, CancellationToken cancellation,
        Action<string>? diagnostic)
    {
        if (!Path.IsPathFullyQualified(path) || new Uri(path).IsUnc || !File.Exists(path)) return null;
        // Formats with additional text/data streams stay on the original system source.
        var pending = Task.Run(() => Open(path, diagnostic));
        try { return await pending.WaitAsync(cancellation); }
        catch {
            _ = pending.ContinueWith(task => {
                if (task.Status == TaskStatus.RanToCompletion) task.Result?.Dispose();
                else _ = task.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    private static WindowsContinuousSource? Open(string path, Action<string>? diagnostic)
    {
        IntPtr sourcePointer = IntPtr.Zero;
        SourceHandle? owner = null;
        try {
            var hr = Create(path, out sourcePointer, out owner);
            if (hr == 1) {
                diagnostic?.Invoke("video continuous source: system fallback for unsupported codec or additional tracks");
                return null;
            }
            Marshal.ThrowExceptionForHR(hr);
            var source = WinRT.MarshalInterface<MediaSource>.FromAbi(sourcePointer);
            var result = new WindowsContinuousSource(source, owner);
            owner = null;
            diagnostic?.Invoke("video continuous source: Windows demux/audio PCM; effectiveMaxRate=4; transportMaxRate=3; residualTempo=MIT; extraPlayers=0");
            return result;
        }
        catch (Exception error) when (error is COMException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) {
            diagnostic?.Invoke($"video continuous source unavailable: hresult=0x{error.HResult:X8}; {error.Message}");
            return null;
        }
        finally {
            if (sourcePointer != IntPtr.Zero) Marshal.Release(sourcePointer);
            owner?.Dispose();
        }
    }

    public void Dispose()
    {
        if (handle.IsClosed) return;
        handle.Dispose(); // Invalidate queued samples before closing the WinRT source.
        Source.Dispose();
    }

    public void ReportDiagnostics(MediaPlaybackSession session, Action<string>? diagnostic)
    {
        if (handle.IsClosed || diagnostic is null) return;
        if (session.PlaybackState != MediaPlaybackState.Playing) { diagnosticsAt = 0; return; }
        var now = Stopwatch.GetTimestamp();
        if (diagnosticsAt != 0 && diagnosticsRate == PlaybackRate && Stopwatch.GetElapsedTime(diagnosticsAt, now).TotalSeconds < 5) return;
        if (ReadStatistics(handle, out var stats) != 0) return;
        var position = MoviePosition(session.Position.TotalSeconds);
        var seconds = diagnosticsAt == 0 ? 0 : Stopwatch.GetElapsedTime(diagnosticsAt, now).TotalSeconds;
        if (seconds >= 5 && diagnosticsRate == PlaybackRate && stats.Epoch == previous.Epoch) {
            var advance = position - diagnosticsPosition;
            diagnostic($"video supply: rate={diagnosticsRate:F2}; clockRate={advance / seconds:F3}; audioSamples={stats.AudioSamples - previous.AudioSamples}; videoSamples={stats.VideoSamples - previous.VideoSamples}; audioRequestMaxMs={stats.AudioRequestMaxMs:F2}; videoRequestMaxMs={stats.VideoRequestMaxMs:F2}; readMaxMs={stats.ReadMaxMs:F2}; audioPtsGaps={stats.AudioGaps - previous.AudioGaps}; audioPtsGapMaxMs={stats.MaxAudioGap / 10000.0:F2}; streamTicks={stats.StreamTicks - previous.StreamTicks}; suppliedAudioAheadWallMs={(stats.AudioEnd / 10000000.0 - position) * 1000 / diagnosticsRate:F1}; presentationAvSync=unmeasured");
        }
        diagnosticsAt = now;
        diagnosticsPosition = position;
        diagnosticsRate = PlaybackRate;
        previous = stats;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceStatistics
    {
        public ulong Epoch, AudioSamples, VideoSamples, AudioGaps, StreamTicks;
        public long AudioEnd, VideoTimestamp, MaxAudioGap, Duration;
        public double AudioRequestMaxMs, VideoRequestMaxMs, ReadMaxMs;
    }

    private sealed class SourceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SourceHandle() : base(true) { }
        protected override bool ReleaseHandle() { WindowsContinuousSource.Close(handle); return true; }
    }
    [DllImport("OFPlayer.Video.MediaSource.dll", EntryPoint = "OFPlayerCreateContinuousSource", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int Create(string path, out IntPtr source, out SourceHandle owner);
    [DllImport("OFPlayer.Video.MediaSource.dll", EntryPoint = "OFPlayerCloseContinuousSource", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern void Close(IntPtr owner);
    [DllImport("OFPlayer.Video.MediaSource.dll", EntryPoint = "OFPlayerReadContinuousStatistics", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int ReadStatistics(SourceHandle owner, out SourceStatistics statistics);
    [DllImport("OFPlayer.Video.MediaSource.dll", EntryPoint = "OFPlayerSetContinuousRate", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int SetRateNative(SourceHandle owner, double rate);
}
