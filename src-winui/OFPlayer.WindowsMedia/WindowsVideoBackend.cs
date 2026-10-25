using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace OFPlayer.WindowsMedia;

public sealed record VideoTrack(int Index, string Kind, string Label, string Language, bool Selected, string? ExternalPath = null);

public sealed record VideoAudioOutput(string Id, string Name);
public sealed record VideoSubtitleAppearance(double FontScale = 1, string Color = "white", double VerticalPosition = 90, string FontFamily = "")
{
    public VideoSubtitleAppearance Normalized() => new(
        double.IsFinite(FontScale) ? Math.Clamp(FontScale, .5, 2.5) : 1,
        Color == "yellow" ? "yellow" : "white",
        double.IsFinite(VerticalPosition) ? Math.Clamp(VerticalPosition, 10, 95) : 90,
        CleanFontFamily(FontFamily));
    public static string CleanFontFamily(string? value) => new((value ?? "").Trim().Where(c => !char.IsControl(c)).Take(128).ToArray());
}
public sealed record VideoPlaybackSource(MediaPlaybackItem Item, IDisposable Owner, string Decoder,
    Action<double>? SetAudioDelay = null, Action<double>? SetSubtitleDelay = null,
    Func<string, string?, CancellationToken, Task>? AddSubtitleAsync = null);
public sealed record VideoSnapshot(long Generation, long Sequence, string Phase, string Path,
    bool Loaded, bool Paused, bool Seeking, bool CanSeek, double Position, double Duration,
    double Rate, double Volume, uint Width, uint Height, string Error, VideoTrack[] Tracks,
    bool Muted = false, bool Loop = false, double? RepeatStart = null, double? RepeatEnd = null,
    double SubtitleDelay = 0, int Rotation = 0,
    string AudioDeviceId = "", double AudioBalance = 0,
    VideoSubtitleAppearance? SubtitleAppearance = null, bool CanStyleSubtitle = false,
    string Decoder = WindowsVideoBackend.SystemDecoder, bool IsNetwork = false, bool CanReconnect = false,
    bool UsedSoftwareFallback = false, string RecoveryMessage = "", double AudioDelay = 0,
    bool CanAdjustAudioDelay = false, bool CanAdjustSubtitleDelay = false, bool CanAddExternalSubtitle = false,
    bool IsSoftwareDecoded = false, string SubtitleStyleNotice = "");

// The caller owns the execution thread. Windows callbacks always return through post.
// This adapter owns one media item, never an application queue, catalog or playback lease.
public sealed partial class WindowsVideoBackend : IDisposable
{
    public const string SystemDecoder = "系统播放";
    private sealed class PlaybackFailureException(string message, bool canFallback) : IOException(message)
    {
        public bool CanFallback { get; } = canFallback;
    }
    private sealed record TrackChoice(string Language, string Label);
    private sealed record ExternalChoice(string PreparedPath, string Label, string OriginalPath);
    private sealed record ResumeState(double Position, bool Paused, int Rotation, TrackChoice? Audio,
        bool SubtitleOff, TrackChoice? Subtitle, ExternalChoice? External, double AudioDelay, double SubtitleDelay, VideoSubtitleAppearance? Appearance);
    private sealed class Load(long generation, MediaPlayer player, MediaPlaybackItem item, string location, bool isNetwork,
        IDisposable? sourceOwner = null, string decoder = SystemDecoder, VideoPlaybackSource? softwareSource = null)
    {
        public long Generation { get; } = generation;
        public MediaPlayer Player { get; } = player;
        public MediaSource Source { get; } = item.Source;
        public string Location { get; } = location;
        public bool IsNetwork { get; } = isNetwork;
        public MediaPlaybackItem Item { get; } = item;
        public IDisposable? SourceOwner { get; } = sourceOwner;

        public string Decoder { get; } = decoder;
        public VideoPlaybackSource? SoftwareSource { get; } = softwareSource;
        public CancellationTokenSource Lifetime { get; } = new();
        public double AudioDelay { get; set; }
        public double SubtitleDelay { get; set; }
        public bool OpenCompleted { get; set; }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<Action> Unsubscribe { get; } = [];
        public List<IRandomAccessStream> SubtitleStreams { get; } = [];
        public List<TaskCompletionSource> PendingSubtitles { get; } = [];
        public List<TimedMetadataTrack> ExternalSubtitles { get; } = [];
        public Dictionary<TimedMetadataTrack, ExternalChoice> ExternalFiles { get; } = [];
        public Dictionary<TimedMetadataTrack, SubtitleStyleState> SubtitleStyles { get; } = [];
        public TimedMetadataTrack? SubtitleToSelect { get; set; }
    }

    private readonly Action<Action> post;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private Load? active;
    private CancellationTokenSource? pendingOpen;
    private long generation, sequence, audioDeviceRequest, subtitleRequest;
    private bool disposed, loaded, seeking, ended, muted, loop, isNetwork, usedSoftwareFallback, desiredPaused = true;
    private string path = "", phase = "idle", error = "", decoder = SystemDecoder;
    private string recoveryMessage = "";
    private double volume = 0.1, rate = 1, audioBalance;
    private double lastPosition;
    private int lastRotation;
    private double? repeatStart, repeatEnd;
    private DeviceInformation? audioDevice;
    public event Action<MediaPlayer?>? PlayerChanged;
    public event Action<VideoSnapshot>? StateChanged;
    public event Action<string>? Diagnostic;
    // Automatic recovery replaces the renderer, not the logical playback session.
    public event Action<long, long, string, double>? RendererRecovering;
    public VideoSnapshot Snapshot { get { CheckThread(); return Capture(); } }
    public double PlaybackTimeScale => (active?.SourceOwner as WindowsContinuousSource)?.TimeScale ?? 1;
    private static double MoviePosition(Load scope) => scope.SourceOwner is WindowsContinuousSource source
        ? source.MoviePosition(scope.Player.PlaybackSession.Position.TotalSeconds) : scope.Player.PlaybackSession.Position.TotalSeconds;
    private static double MovieDuration(Load scope) => scope.SourceOwner is WindowsContinuousSource source
        ? source.Duration : scope.Player.PlaybackSession.NaturalDuration.TotalSeconds;
    // The factory runs on the playback thread and transfers ownership of its returned source.
    public Func<string, CancellationToken, Task<VideoPlaybackSource>>? SoftwareSourceFactory { get; set; }
    public bool AutoSoftwareFallback { get; set; } = true;

    public WindowsVideoBackend(Action<Action> post) => this.post = post;

    private void CheckThread()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (ownerThread != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Video commands must run on the owner's execution thread.");
    }

    private void Dispatch(Load scope, Action action) => post(() => {
        if (disposed || active != scope || generation != scope.Generation) return;
        action();
        Publish();
    });



    public Task OpenAsync(string filePath, bool autoPlay = true, bool softwareDecode = false)
    {
        CheckThread();
        filePath = LocalMediaPath.Validate(filePath);
        var factory = softwareDecode ? SoftwareSourceFactory
            ?? throw new NotSupportedException("这个版本没有兼容模式，当前视频会继续播放。") : null;
        if (softwareDecode && (!System.IO.Path.IsPathFullyQualified(filePath) || !File.Exists(filePath)))
            throw new FileNotFoundException("兼容模式只能播放本机上的视频文件。", filePath);
        return OpenSourceAsync(filePath, false, autoPlay, factory);
    }





    private ResumeState CaptureResumeState()
    {
        RememberPosition();
        var scope = active;
        var audioIndex = scope?.Item.AudioTracks.SelectedIndex ?? -1;
        var audio = scope is not null && audioIndex >= 0 && audioIndex < scope.Item.AudioTracks.Count ? scope.Item.AudioTracks[audioIndex] : null;
        var subtitle = scope is null ? null : SelectedSubtitle(scope);
        return new(lastPosition, desiredPaused, lastRotation,
            audio is null ? null : new(audio.Language, audio.Label),
            subtitle is null, subtitle is null ? null : new(subtitle.Language, subtitle.Label),
            subtitle is null ? null : scope!.ExternalFiles.GetValueOrDefault(subtitle), scope?.AudioDelay ?? 0, subtitle is null ? 0 : scope!.SubtitleStyles.GetValueOrDefault(subtitle)?.Delay ?? scope.SubtitleDelay,
            subtitle is null ? null : scope!.SubtitleStyles.GetValueOrDefault(subtitle)?.Applied);
    }

    private void RememberPosition()
    {
        if (!loaded || active is null || seeking || error.Length != 0) return;
        var session = active.Player.PlaybackSession;
        if (session.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Paused)
            lastPosition = Math.Max(0, MoviePosition(active));
    }



    private static async Task<MediaSource> CreateSourceAsync(string location, bool isNetwork, CancellationToken cancellation)
    {

        location = LocalMediaPath.Validate(location);
        if (!System.IO.Path.IsPathFullyQualified(location) || !File.Exists(location))
            throw new FileNotFoundException("视频文件不存在或路径不是绝对路径。", location);
        var file = await StorageFile.GetFileFromPathAsync(location).AsTask(cancellation);
        return MediaSource.CreateFromStorageFile(file);
    }

    private async Task OpenSourceAsync(string location, bool isNetwork, bool autoPlay,
        Func<string, CancellationToken, Task<VideoPlaybackSource>>? softwareFactory = null,
        ResumeState? resume = null, bool reconnect = false, bool fallback = false, long? recoveringGeneration = null)
    {
        CheckThread();
        var request = ++generation;
        CancelPendingOpen();
        ReleaseActive();
        var cancellation = new CancellationTokenSource();
        pendingOpen = cancellation;
        loaded = seeking = ended = false;
        repeatStart = repeatEnd = null;
        this.isNetwork = isNetwork;
        usedSoftwareFallback = fallback;
        desiredPaused = resume?.Paused ?? !autoPlay;
        lastPosition = resume?.Position ?? 0;
        lastRotation = resume?.Rotation ?? 0;
        path = location;
        phase = reconnect ? "reconnecting" : "opening";
        error = "";
        recoveryMessage = fallback ? "系统播放不了这个视频，正在改用兼容模式…" : reconnect ? "正在重新连接网络视频…" : "";
        decoder = softwareFactory is null ? SystemDecoder : "兼容模式";
        MediaPlayer? unownedPlayer = null;
        MediaSource? unownedSource = null;
        WindowsContinuousSource? unownedContinuous = null;
        VideoPlaybackSource? unownedSoftware = null;

        Task<VideoPlaybackSource>? waitingForFactory = null;
        try {
            if (recoveringGeneration is long previousGeneration)
                RendererRecovering?.Invoke(previousGeneration, request, location, lastPosition);
            Publish();
            MediaPlaybackItem item;
            if (softwareFactory is not null) {
                waitingForFactory = softwareFactory(location, cancellation.Token);
                unownedSoftware = await waitingForFactory.WaitAsync(TimeSpan.FromSeconds(30), cancellation.Token);
                waitingForFactory = null;
                item = unownedSoftware.Item;
            }
            else {
                unownedContinuous = await WindowsContinuousSource.TryOpenAsync(location, cancellation.Token, Diagnostic);
                unownedSource = unownedContinuous?.Source ?? await CreateSourceAsync(location, isNetwork, cancellation.Token);
                item = new MediaPlaybackItem(unownedSource);
            }
            if (disposed || request != generation) throw new OperationCanceledException("Video open superseded.");
            var player = unownedPlayer = new MediaPlayer {
                AutoPlay = false, Volume = volume, IsMuted = muted, IsLoopingEnabled = loop,
                AudioBalance = audioBalance, AudioDevice = audioDevice!
            };
            // The product's existing WinUI bridge remains the sole SMTC command owner.
            player.CommandManager.IsEnabled = false;
            var scope = new Load(request, player, item, location, isNetwork,
                unownedSoftware?.Owner ?? (IDisposable?)unownedContinuous, unownedSoftware?.Decoder ?? SystemDecoder, unownedSoftware);


            active = scope;
            decoder = scope.Decoder;
            unownedPlayer = null;
            unownedSource = null;
            unownedSoftware = null;
            unownedContinuous = null;
            Subscribe(scope);
            PlayerChanged?.Invoke(player);
            player.Source = scope.Item;
            await scope.Ready.Task.WaitAsync(TimeSpan.FromSeconds(isNetwork ? 30 : 15), cancellation.Token);
            if (disposed || active != scope || request != generation) throw new OperationCanceledException("Video open superseded.");
            if (scope.SourceOwner is WindowsContinuousSource) SetRateCore(scope, rate);
            else if (player.PlaybackSession.IsSupportedPlaybackRateRange(rate, rate)) player.PlaybackSession.PlaybackRate = rate;
            else rate = player.PlaybackSession.PlaybackRate;
            if (lastRotation != 0 && !player.IsVideoFrameServerEnabled) player.PlaybackSession.PlaybackRotation = (MediaRotation)(lastRotation / 90);
            var trackRestoreWarning = "";
            if (resume is not null) {
                try {
                    var audioMatches = Enumerable.Range(0, item.AudioTracks.Count).Where(i => Matches(resume.Audio, item.AudioTracks[i].Language, item.AudioTracks[i].Label)).ToArray();
                    if (audioMatches.Length == 1) item.AudioTracks.SelectedIndex = audioMatches[0];
                    else if (resume.Audio is not null) trackRestoreWarning += " 上次音轨无法唯一匹配，请重新选择。";
                    // Keep subtitle-off authoritative while any external restore is in flight.
                    SelectSubtitleCore(scope, -1);
                    if (!resume.SubtitleOff) {
                        if (resume.External is { } external) {
                            await AddSubtitleAsync(external.PreparedPath, external.Label, external.OriginalPath);
                            if (disposed || active != scope || request != generation) throw new OperationCanceledException("Video restore superseded.");
                        }
                        else {
                            var matches = Enumerable.Range(0, item.TimedMetadataTracks.Count).Where(i => IsSubtitle(item.TimedMetadataTracks[i]) && Matches(resume.Subtitle, item.TimedMetadataTracks[i].Language, item.TimedMetadataTracks[i].Label)).ToArray();
                            if (matches.Length == 1) SelectSubtitleCore(scope, matches[0]);
                            else trackRestoreWarning += " 上次字幕无法唯一匹配，已关闭字幕。";
                        }
                    }
                    if (resume.Appearance is { } appearance && CurrentSubtitleStyle() is not null) ApplySubtitleStyle(appearance);
                    if (scope.SoftwareSource?.SetAudioDelay is { } audioDelay) { audioDelay(resume.AudioDelay); scope.AudioDelay = resume.AudioDelay; }
                    if (scope.SoftwareSource?.SetSubtitleDelay is { } subtitleDelay) { subtitleDelay(resume.SubtitleDelay); scope.SubtitleDelay = resume.SubtitleDelay; }
                    else if (CurrentSubtitleStyle() is not null && resume.SubtitleDelay != 0) ApplyPlainSubtitleDelay(resume.SubtitleDelay);
                }
                catch (OperationCanceledException) {
                    if (disposed || active != scope || request != generation) throw;
                    // An explicit newer subtitle choice wins, without failing video recovery.
                }
                catch (Exception exception) { trackRestoreWarning += " 轨道恢复失败：" + exception.Message; }
            }
            if (disposed || active != scope || request != generation) throw new OperationCanceledException("Video restore superseded.");
            var restoredPosition = false;
            if (resume is { Position: > 0 } && player.PlaybackSession.CanSeek && player.PlaybackSession.NaturalDuration > TimeSpan.Zero) {
                try { SeekCore(scope, resume.Position); restoredPosition = true; }
                catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException) {
                    System.Diagnostics.Trace.WriteLine($"Video position restore failed: {exception.Message}");
                }
            }
            scope.OpenCompleted = true;
            if (!desiredPaused) player.Play();
            recoveryMessage = fallback ? "系统播放不了这个视频，已自动改用兼容模式。"
                : reconnect ? restoredPosition ? "已重新连接，已恢复上次播放位置。" : "已重新连接。" : "";
            if (resume is { Position: > 0 } && !restoredPosition && player.PlaybackSession.NaturalDuration > TimeSpan.Zero)
                recoveryMessage += " 此来源未能恢复上次播放位置。";
            recoveryMessage += trackRestoreWarning;
            Publish();
        }
        catch (Exception exception) {
            cancellation.Cancel();
            if (unownedPlayer is not null) unownedPlayer.Source = null;
            unownedPlayer?.Dispose();
            if (unownedContinuous is not null) unownedContinuous.Dispose();
            else unownedSource?.Dispose();
            unownedSoftware?.Owner.Dispose();

            if (!disposed && request == generation) {
                var fallbackFactory = SoftwareSourceFactory;
                if (!isNetwork && softwareFactory is null && AutoSoftwareFallback && fallbackFactory is not null &&
                    File.Exists(location) && (exception is PlaybackFailureException { CanFallback: true } or TimeoutException)) {
                    var restore = CaptureResumeState();
                    await OpenSourceAsync(location, false, !restore.Paused, fallbackFactory, restore, fallback: true);
                    return;
                }
                ReleaseActive();
                loaded = seeking = ended = false;
                phase = "failed";
                if (fallback) recoveryMessage = "系统播放和兼容模式都打不开这个视频。";
                else if (reconnect) recoveryMessage = "网络视频重连失败，可以再次重试。";
                error = exception is TimeoutException
                    ? isNetwork ? "网络视频打开超时，请检查地址与网络连接。"
                        : softwareFactory is not null ? "兼容模式打开视频超时。" : "视频打开超时，请重试。"
                    : exception.Message;
                Publish();
                if (exception is TimeoutException) throw new TimeoutException(error, exception);
            }
            throw;
        }
        finally {
            if (pendingOpen == cancellation) {
                pendingOpen = null;
                if (!disposed && request == generation) Publish();
            }
            if (waitingForFactory is not null)
                _ = DisposeAbandonedSoftwareSourceAsync(waitingForFactory, cancellation);
            else cancellation.Dispose();
        }
    }

    private async Task RecoverWithSoftwareAsync(Load failed, Func<string, CancellationToken, Task<VideoPlaybackSource>> factory)
    {
        var restore = CaptureResumeState();
        try { await OpenSourceAsync(failed.Location, false, !restore.Paused, factory, restore, fallback: true, recoveringGeneration: failed.Generation); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { System.Diagnostics.Trace.WriteLine($"Video software recovery failed: {exception.Message}"); }
    }

    private void CancelPendingOpen()
    {
        var pending = pendingOpen;
        pendingOpen = null;
        pending?.Cancel();
    }

    private static async Task DisposeAbandonedSoftwareSourceAsync(Task<VideoPlaybackSource> pending,
        CancellationTokenSource cancellation)
    {
        try {
            VideoPlaybackSource source;
            try { source = await pending.ConfigureAwait(false); }
            catch { return; } // The open operation already reported this failure or cancellation.
            // A factory can finish after timeout or window close. This item was never bound.
            try { source.Owner.Dispose(); }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine($"Video source cleanup failed: {exception}"); }
        }
        finally { cancellation.Dispose(); }
    }

    private void Subscribe(Load scope)
    {
        var player = scope.Player;
        var session = player.PlaybackSession;
        TypedEventHandler<MediaPlayer, object> opened = (_, _) => Dispatch(scope, () => {
            loaded = true;
            phase = "paused";
            scope.Ready.TrySetResult();
        });
        TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> failed = (sender, args) => {
            var canFallback = args.Error is MediaPlayerError.DecodingError or MediaPlayerError.SourceNotSupported or MediaPlayerError.Unknown;
            var code = args.ExtendedErrorCode?.HResult ?? 0;
            var detail = $"video renderer failed: generation={scope.Generation}; backend={scope.Decoder}; error={args.Error}; hresult=0x{code:X8}; detail={args.ErrorMessage}";
            System.Diagnostics.Trace.WriteLine(detail);
            Diagnostic?.Invoke(detail);
            var message = scope.SoftwareSource is not null
                ? "兼容模式也无法播放这个文件，文件可能已损坏。"
                : scope.IsNetwork
                ? "这个网络视频播放不了，请检查网络后重试。"
                : $"系统播放器无法播放这个视频（{args.Error}，0x{code:X8}）。";
            Dispatch(scope, () => {
                if (scope.OpenCompleted && !scope.IsNetwork && scope.SoftwareSource is null && canFallback &&
                    AutoSoftwareFallback && SoftwareSourceFactory is { } factory && File.Exists(scope.Location)) {
                    _ = RecoverWithSoftwareAsync(scope, factory);
                    return;
                }
                loaded = seeking = ended = false;
                phase = "failed";
                error = message;
                scope.Ready.TrySetException(new PlaybackFailureException(message, canFallback));
                player.Pause();
            });
        };
        TypedEventHandler<MediaPlayer, object> finished = (_, _) => Dispatch(scope, () => {
            seeking = false;
            if (error.Length != 0) return;
            if (session.CanSeek && (repeatEnd.HasValue || loop)) {
                if (!TryRepeatSeek(scope, repeatEnd.HasValue ? repeatStart!.Value : 0)) return;
                player.Play();
                phase = "playing";
                return;
            }
            ended = true;
            desiredPaused = true;
            phase = "ended";
        });
        TypedEventHandler<MediaPlaybackSession, object> state = (_, _) => Dispatch(scope, () => {
            Diagnostic?.Invoke($"video clock: state={session.PlaybackState}; transportRate={session.PlaybackRate:F2}; timeScale={(scope.SourceOwner as WindowsContinuousSource)?.TimeScale ?? 1:F3}; position={MoviePosition(scope):F3}; seeking={seeking}");
            if (error.Length == 0 && !ended && (scope.OpenCompleted || phase != "reconnecting"))
                phase = session.PlaybackState.ToString().ToLowerInvariant();
        });
        TypedEventHandler<MediaPlaybackSession, object> changed = (_, _) => Dispatch(scope, () => { });
        TypedEventHandler<MediaPlaybackSession, object> position = (_, _) => Dispatch(scope, () => RepeatIfNeeded(scope));
        TypedEventHandler<MediaPlaybackSession, object> seekDone = (_, _) => Dispatch(scope, () => seeking = false);
        TypedEventHandler<MediaPlaybackItem, IVectorChangedEventArgs> tracks = (_, _) => Dispatch(scope, () => SelectAddedSubtitle(scope));
        player.MediaOpened += opened;
        player.MediaFailed += failed;
        player.MediaEnded += finished;
        session.PlaybackStateChanged += state;
        session.NaturalDurationChanged += changed;
        session.NaturalVideoSizeChanged += changed;
        session.SeekCompleted += seekDone;
        session.PositionChanged += position;
        session.PlaybackRateChanged += changed;
        scope.Item.AudioTracksChanged += tracks;
        scope.Item.TimedMetadataTracksChanged += tracks;
        scope.Unsubscribe.Add(() => {
            player.MediaOpened -= opened;
            player.MediaFailed -= failed;
            player.MediaEnded -= finished;
            session.PlaybackStateChanged -= state;
            session.NaturalDurationChanged -= changed;
            session.NaturalVideoSizeChanged -= changed;
            session.SeekCompleted -= seekDone;
            session.PositionChanged -= position;
            session.PlaybackRateChanged -= changed;
            scope.Item.AudioTracksChanged -= tracks;
            scope.Item.TimedMetadataTracksChanged -= tracks;
        });
    }

    public void Refresh()
    {
        CheckThread();
        if (active is not null) {
            RepeatIfNeeded(active);
            if (active.SourceOwner is WindowsContinuousSource source)
                source.ReportDiagnostics(active.Player.PlaybackSession, Diagnostic);
        }
        Publish();
    }
    private Load RequireLoaded()
    {
        CheckThread();
        if (!loaded || active is null) throw new InvalidOperationException("请先载入视频。");
        return active;
    }
    public void SetPaused(bool paused)
    {
        var scope = RequireLoaded();
        if (paused) {
            scope.Player.Pause();
            if (!ended) phase = "paused";
        }
        else {
            if (ended) SeekCore(scope, repeatEnd.HasValue ? repeatStart!.Value : 0);
            ended = false;
            scope.Player.Play();
            phase = "playing";
        }
        desiredPaused = paused;
        Publish();
    }
    public void Seek(double seconds)
    {
        var scope = RequireLoaded();
        if (!double.IsFinite(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (!scope.Player.PlaybackSession.CanSeek) throw new InvalidOperationException("此来源不支持定位。");
        if (scope.Player.PlaybackSession.NaturalDuration <= TimeSpan.Zero)
            throw new InvalidOperationException("此直播或未知时长来源暂不支持按时间定位。");
        SeekCore(scope, seconds);
        Publish();
    }
    private void SeekCore(Load scope, double seconds)
    {
        var session = scope.Player.PlaybackSession;
        ended = false;
        phase = session.PlaybackState.ToString().ToLowerInvariant();
        var movieTarget = Math.Clamp(seconds, 0, Math.Max(0, MovieDuration(scope)));
        var target = TimeSpan.FromSeconds(scope.SourceOwner is WindowsContinuousSource source ? source.MediaPosition(movieTarget) : movieTarget);
        if (Math.Abs((session.Position - target).TotalMilliseconds) < 0.1) {
            seeking = false;
            return;
        }
        seeking = true;
        try {
            session.Position = target;
            lastPosition = movieTarget;
        }
        catch { seeking = false; throw; }
    }
    public void SetRate(double value)
    {
        var scope = RequireLoaded();
        if (!double.IsFinite(value) || value < 0.25 || value > 4) throw new ArgumentOutOfRangeException(nameof(value));
        if (scope.SourceOwner is not WindowsContinuousSource && !scope.Player.PlaybackSession.IsSupportedPlaybackRateRange(value, value))
            throw new InvalidOperationException("Windows 播放器不支持此速度。");
        SetRateCore(scope, value);
        rate = value;
        Publish();
    }
    private void SetRateCore(Load scope, double value)
    {
        var session = scope.Player.PlaybackSession;
        if (scope.SourceOwner is not WindowsContinuousSource source) { session.PlaybackRate = value; return; }
        var position = MoviePosition(scope);
        var oldScale = source.TimeScale;
        var oldRate = source.PlaybackRate;
        var playing = session.PlaybackState == MediaPlaybackState.Playing;
        if (oldScale != Math.Max(1, value / 3)) scope.Player.Pause();
        var subtitlesRetimed = false;
        try {
            session.PlaybackRate = source.SetRate(value);
            if (source.TimeScale != oldScale) {
                RescaleSubtitleClock(scope, oldScale, source.TimeScale);
                subtitlesRetimed = true;
                session.Position = TimeSpan.FromSeconds(source.MediaPosition(position));
                if (playing) scope.Player.Play();
            }
        }
        catch {
            var failedScale = source.TimeScale;
            try {
                session.PlaybackRate = source.SetRate(oldRate);
                if (subtitlesRetimed) RescaleSubtitleClock(scope, failedScale, oldScale);
                session.Position = TimeSpan.FromSeconds(source.MediaPosition(position));
                if (playing) scope.Player.Play();
            }
            catch (Exception rollback) { Diagnostic?.Invoke($"video rate restore failed: hresult=0x{rollback.HResult:X8}"); }
            throw;
        }
    }
    public void SetMuted(bool value)
    {
        CheckThread();
        muted = value;
        if (active is not null) active.Player.IsMuted = value;
        Publish();
    }
    public void SetLoop(bool value)
    {
        CheckThread();
        loop = value;
        if (active is not null) active.Player.IsLoopingEnabled = value && !repeatEnd.HasValue;
        Publish();
    }
    public void SetABRepeat(double? start, double? end)
    {
        CheckThread();
        if (start is null && end is null) {
            repeatStart = repeatEnd = null;
            if (active is not null) active.Player.IsLoopingEnabled = loop;
            Publish();
            return;
        }
        var scope = RequireLoaded();
        var session = scope.Player.PlaybackSession;
        if (!session.CanSeek) throw new InvalidOperationException("此来源不支持 A–B 循环。");
        var duration = MovieDuration(scope);
        if (start is null || !double.IsFinite(start.Value) || start < 0 || start >= duration)
            throw new ArgumentOutOfRangeException(nameof(start), "A 点必须位于视频时长之内。");
        if (end is not null && (!double.IsFinite(end.Value) || end <= start || end > duration))
            throw new ArgumentOutOfRangeException(nameof(end), "B 点必须晚于 A 点且不超过视频时长。");
        // Reject a failed initial seek before installing a repeating range.
        if (end.HasValue && (MoviePosition(scope) < start || MoviePosition(scope) >= end))
            SeekCore(scope, start.Value);
        scope.Player.IsLoopingEnabled = loop && end is null;
        repeatStart = start;
        repeatEnd = end;
        Publish();
    }
    private void RepeatIfNeeded(Load scope)
    {
        var session = scope.Player.PlaybackSession;
        if (!loaded || seeking || ended || error.Length != 0 || repeatStart is null || repeatEnd is null ||
            !session.CanSeek || session.PlaybackState != MediaPlaybackState.Playing) return;
        if (MoviePosition(scope) >= repeatEnd.Value) TryRepeatSeek(scope, repeatStart.Value);
    }
    private bool TryRepeatSeek(Load scope, double position)
    {
        try { SeekCore(scope, position); return true; }
        catch (Exception exception) {
            repeatStart = repeatEnd = null;
            loop = false;
            scope.Player.IsLoopingEnabled = false;
            scope.Player.Pause();
            desiredPaused = true;
            recoveryMessage = $"循环定位失败，已暂停并取消循环：{exception.Message}";
            return false;
        }
    }
    public void StepFrame(bool backward = false)
    {
        var scope = RequireLoaded();
        var session = scope.Player.PlaybackSession;
        if (!session.CanSeek || session.NaturalVideoWidth == 0)
            throw new InvalidOperationException("此来源不支持逐帧定位。");
        scope.Player.Pause();
        desiredPaused = true;
        // Windows' backward API moves approximately 42 ms, independent of the source frame rate.
        if (backward) scope.Player.StepBackwardOneFrame();
        else scope.Player.StepForwardOneFrame();
        ended = seeking = false;
        phase = "paused";
        Publish();
    }
    public void SetRotation(int degrees)
    {
        var scope = RequireLoaded();
        if (degrees is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(degrees));
        // Frame-server output stays unrotated; its presenter applies the same rotation to
        // both the picture and glass. The normal system presenter handles its own rotation.
        if (!scope.Player.IsVideoFrameServerEnabled) scope.Player.PlaybackSession.PlaybackRotation = (MediaRotation)(degrees / 90);
        lastRotation = degrees;
        Publish();
    }
    public void SetVolume(double value)
    {
        CheckThread();
        if (!double.IsFinite(value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(value));
        volume = value;
        if (active is not null) active.Player.Volume = value;
        Publish();
    }
    public void SelectAudio(int index)
    {
        var scope = RequireLoaded();
        if (index < 0 || index >= scope.Item.AudioTracks.Count) throw new ArgumentOutOfRangeException(nameof(index));
        scope.Item.AudioTracks.SelectedIndex = index;
        Publish();
    }
    public async Task<VideoAudioOutput[]> GetAudioOutputsAsync()
    {
        CheckThread();
        var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
        CheckThread();
        return [new("", "系统默认输出"), .. devices.Where(device => device.IsEnabled)
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(device => new VideoAudioOutput(device.Id, device.Name))];
    }
    public async Task SetAudioOutputAsync(string? deviceId)
    {
        CheckThread();
        var request = ++audioDeviceRequest;
        DeviceInformation? selected = null;
        if (!string.IsNullOrWhiteSpace(deviceId)) {
            var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
            CheckThread();
            if (request != audioDeviceRequest) throw new OperationCanceledException("Audio output selection superseded.");
            selected = devices.FirstOrDefault(device => device.IsEnabled && device.Id == deviceId)
                ?? throw new InvalidOperationException("此音频输出设备已不可用，请刷新设备列表后重新选择。");
        }
        if (active is not null) active.Player.AudioDevice = selected!;
        audioDevice = selected;
        Publish();
    }
    public void SetAudioBalance(double balance)
    {
        CheckThread();
        if (!double.IsFinite(balance) || balance < -1 || balance > 1)
            throw new ArgumentOutOfRangeException(nameof(balance));
        if (active is not null) active.Player.AudioBalance = balance;
        audioBalance = balance;
        Publish();
    }
    public void SetAudioDelay(double seconds)
    {
        var scope = RequireLoaded();
        if (!double.IsFinite(seconds) || Math.Abs(seconds) > 10)
            throw new ArgumentOutOfRangeException(nameof(seconds), "音频偏移范围为 -10 至 10 秒。");
        if (scope.SoftwareSource?.SetAudioDelay is not { } setDelay || scope.Item.AudioTracks.Count == 0)
            throw new NotSupportedException("这个视频不能调整音画同步，切到兼容模式后可调。");
        setDelay(seconds);
        scope.AudioDelay = seconds;
        Publish();
    }
    public void SelectSubtitle(int index)
    {
        var scope = RequireLoaded();
        var tracks = scope.Item.TimedMetadataTracks;
        if (index < -1 || index >= tracks.Count || (index >= 0 && !IsSubtitle(tracks[index])))
            throw new ArgumentOutOfRangeException(nameof(index));
        ++subtitleRequest;
        foreach (var pending in scope.PendingSubtitles.ToArray())
            pending.TrySetException(new OperationCanceledException("Subtitle selection superseded pending load."));
        scope.SubtitleToSelect = null;
        SelectSubtitleCore(scope, index);
        Publish();
    }
    private static void SelectSubtitleCore(Load scope, int index)
    {
        var tracks = scope.Item.TimedMetadataTracks;
        for (var i = 0; i < tracks.Count; i++) {
            if (IsSubtitle(tracks[i])) tracks.SetPresentationMode((uint)i,
                i == index ? TimedMetadataTrackPresentationMode.PlatformPresented : TimedMetadataTrackPresentationMode.Disabled);
        }
    }
    private static void SelectAddedSubtitle(Load scope)
    {
        if (scope.SubtitleToSelect is null) return;
        for (var i = 0; i < scope.Item.TimedMetadataTracks.Count; i++) {
            if (scope.Item.TimedMetadataTracks[i] != scope.SubtitleToSelect) continue;
            SelectSubtitleCore(scope, i);
            scope.SubtitleToSelect = null;
            break;
        }
    }
    private static TimedMetadataTrack? SelectedExternalSubtitle(Load scope)
    {
        var tracks = scope.Item.TimedMetadataTracks;
        for (var i = 0; i < tracks.Count; i++) {
            if (IsSubtitle(tracks[i]) && tracks.GetPresentationMode((uint)i) == TimedMetadataTrackPresentationMode.PlatformPresented)
                return scope.ExternalSubtitles.Find(subtitle => subtitle == tracks[i]);
        }
        return null;
    }
    private static TimedMetadataTrack? SelectedSubtitle(Load scope)
    {
        var tracks = scope.Item.TimedMetadataTracks;
        for (var i = 0; i < tracks.Count; i++) {
            if (IsSubtitle(tracks[i]) && tracks.GetPresentationMode((uint)i) == TimedMetadataTrackPresentationMode.PlatformPresented)
                return tracks[i];
        }
        return null;
    }
    private static bool HasSelectedSubtitle(Load scope) => SelectedSubtitle(scope) is not null;
    public void SetSubtitleDelay(double seconds)
    {
        var scope = RequireLoaded();
        if (!double.IsFinite(seconds) || Math.Abs(seconds) > 600)
            throw new ArgumentOutOfRangeException(nameof(seconds), "字幕偏移范围为 -600 至 600 秒。");
        if (scope.SoftwareSource?.SetSubtitleDelay is { } setDelay) {
            if (!HasSelectedSubtitle(scope)) throw new NotSupportedException("请先选择要显示的字幕。");
            setDelay(seconds);
            scope.SubtitleDelay = seconds;
            Publish();
            return;
        }
        ApplyPlainSubtitleDelay(seconds);
        Publish();
    }
    public void SetSubtitleAppearance(double fontScale, string color, double verticalPosition, string? fontFamily = null)
    {
        CheckThread();
        if (!double.IsFinite(fontScale) || fontScale < 0.5 || fontScale > 2.5)
            throw new ArgumentOutOfRangeException(nameof(fontScale), "字幕字号范围为 50% 至 250%。");
        if (!double.IsFinite(verticalPosition) || verticalPosition < 10 || verticalPosition > 95)
            throw new ArgumentOutOfRangeException(nameof(verticalPosition), "字幕垂直位置范围为 10% 至 95%。");
        color = color?.ToLowerInvariant() ?? "";
        if (color is not ("white" or "yellow")) throw new ArgumentOutOfRangeException(nameof(color));
        // Slider/color edits preserve the currently chosen font; an explicit empty name restores the parser font.
        fontFamily ??= CurrentSubtitleStyle()?.Applied?.FontFamily ?? "";
        if (fontFamily.Length > 128 || fontFamily.Any(char.IsControl)) throw new ArgumentOutOfRangeException(nameof(fontFamily));
        ApplySubtitleStyle(new(fontScale, color, verticalPosition, fontFamily.Trim()));
        Publish();
    }
    public string GetSubtitleDiagnostics()
    {
        CheckThread();
        var subtitle = active is not null ? SelectedExternalSubtitle(active) : null;
        if (subtitle is null) return "No external subtitle selected.";
        var first = subtitle.Cues.FirstOrDefault();
        var textLength = (first as TimedTextCue)?.Lines.Sum(line => line.Text.Length) ?? 0;
        return $"cues={subtitle.Cues.Count}; active={subtitle.ActiveCues.Count}; " +
            $"firstStart={first?.StartTime.TotalSeconds:0.###}; firstDuration={first?.Duration.TotalSeconds:0.###}; firstTextLength={textLength}";
    }
    public async Task AddSubtitleAsync(string filePath, string? label = null, string? originalPath = null)
    {
        var scope = RequireLoaded();
        if (scope.SoftwareSource?.AddSubtitleAsync is { } addSubtitle) {
            var softwareRequest = ++subtitleRequest;
            var previousTracks = scope.Item.TimedMetadataTracks.ToHashSet();
            await addSubtitle(filePath, label ?? System.IO.Path.GetFileName(filePath), scope.Lifetime.Token);
            if (disposed || active != scope || softwareRequest != subtitleRequest)
                throw new OperationCanceledException("Subtitle load superseded.");
            var added = scope.Item.TimedMetadataTracks.FirstOrDefault(track => IsSubtitle(track) && !previousTracks.Contains(track))
                ?? throw new InvalidDataException("解码器未返回可显示的新字幕轨道。");
            scope.ExternalFiles[added] = new(filePath, label ?? System.IO.Path.GetFileName(filePath), originalPath ?? filePath);
            scope.SubtitleToSelect = added;
            SelectAddedSubtitle(scope);
            scope.SoftwareSource.SetSubtitleDelay?.Invoke(scope.SubtitleDelay);
            Publish();
            return;
        }
        if (scope.SoftwareSource is not null)
            throw new NotSupportedException("兼容模式下暂时只能用视频自带的字幕，当前视频会继续播放。");
        if (scope.IsNetwork && (!scope.Player.PlaybackSession.CanSeek || scope.Player.PlaybackSession.NaturalDuration <= TimeSpan.Zero))
            throw new NotSupportedException("直播或无法定位的网络视频暂不支持外挂字幕，当前视频会继续播放。");
        var request = ++subtitleRequest;
        if (!new[] { ".srt", ".vtt" }.Contains(System.IO.Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException("首版外部字幕支持 SRT 和 WebVTT；不加载 ASS/SSA 引擎。");
        var file = await StorageFile.GetFileFromPathAsync(filePath);
        var stream = await file.OpenReadAsync();
        if (disposed || active != scope || request != subtitleRequest) { stream.Dispose(); throw new OperationCanceledException("Subtitle load superseded."); }
        scope.SubtitleStreams.Add(stream);
        var text = TimedTextSource.CreateFromStream(stream);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.PendingSubtitles.Add(ready);
        var abandoned = false;
        TimedMetadataTrack? selected = null;
        List<TimedMetadataTrack> addedSubtitles = [];
        void CompleteSelection()
        {
            if (ready.Task.IsCompleted) return;
            if (abandoned || request != subtitleRequest) {
                ready.TrySetException(new OperationCanceledException("Subtitle load superseded."));
                return;
            }
            if (selected is null) return;
            try {
                scope.SubtitleToSelect = selected;
                SelectAddedSubtitle(scope);
                if (scope.SubtitleToSelect is null) ready.TrySetResult();
            }
            catch (Exception exception) { ready.TrySetException(exception); }
        }
        TypedEventHandler<TimedTextSource, TimedTextSourceResolveResultEventArgs> resolved = (_, args) => Dispatch(scope, () => {
            if (abandoned || request != subtitleRequest) { CompleteSelection(); return; }
            try {
                if (args.Error is not null) throw new InvalidDataException($"Windows 无法解析字幕：{args.Error.ErrorCode}");
                var subtitles = args.Tracks.Where(IsSubtitle).ToArray();
                if (subtitles.Length == 0) {
                    ready.TrySetException(new InvalidDataException("字幕文件中没有可用的字幕轨道。"));
                    return;
                }
                // Keep the system-parsed tracks attached to this source. Copying their cues to
                // a replacement player loses native subtitle scheduling and paused seek state.
                foreach (var track in subtitles) {
                    track.Label = label ?? System.IO.Path.GetFileName(filePath);
                    scope.ExternalSubtitles.Add(track);
                    addedSubtitles.Add(track);
                }
                selected = subtitles[0];
                CompleteSelection();
            }
            catch (Exception exception) { ready.TrySetException(exception); }
        });
        // Resolved and TimedMetadataTracksChanged can arrive in either order. Finish only
        // after the original track is in the playback item's list and platform-presented.
        TypedEventHandler<MediaPlaybackItem, IVectorChangedEventArgs> tracksChanged = (_, _) => Dispatch(scope, CompleteSelection);
        text.Resolved += resolved;
        scope.Item.TimedMetadataTracksChanged += tracksChanged;
        Action unsubscribe = () => {
            text.Resolved -= resolved;
            scope.Item.TimedMetadataTracksChanged -= tracksChanged;
        };
        scope.Unsubscribe.Add(unsubscribe);
        try {
            scope.Source.ExternalTimedTextSources.Add(text);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (disposed || active != scope || request != subtitleRequest) throw new OperationCanceledException("Subtitle load superseded.");
        }
        catch {
            abandoned = true;
            if (!disposed && active == scope) {
                if (scope.SubtitleToSelect == selected) scope.SubtitleToSelect = null;
                scope.Source.ExternalTimedTextSources.Remove(text);
                foreach (var track in addedSubtitles) scope.ExternalSubtitles.Remove(track);
                scope.SubtitleStreams.Remove(stream);
                stream.Dispose();
            }
            throw;
        }
        finally {
            unsubscribe();
            scope.Unsubscribe.Remove(unsubscribe);
            scope.PendingSubtitles.Remove(ready);
        }
        if (selected is not null) {
            scope.ExternalFiles[selected] = new(filePath, label ?? System.IO.Path.GetFileName(filePath), originalPath ?? filePath);
            RegisterPlainSubtitleStyle(scope, selected);
        }
        Publish();
    }

    private static bool Matches(TrackChoice? choice, string language, string label)
        => choice is not null && (!string.IsNullOrEmpty(choice.Language) || !string.IsNullOrEmpty(choice.Label))
            && string.Equals(choice.Language, language, StringComparison.OrdinalIgnoreCase) && choice.Label == label;
    private static bool IsSubtitle(TimedMetadataTrack track) => track.TimedMetadataKind is TimedMetadataKind.Subtitle or TimedMetadataKind.Caption;
    private VideoSnapshot Capture()
    {
        RememberPosition();
        var session = active?.Player.PlaybackSession;
        var canAdjustSoftwareSubtitle = loaded && active?.SoftwareSource?.SetSubtitleDelay is not null && HasSelectedSubtitle(active);
        var plainSubtitle = CurrentSubtitleStyle();
        List<VideoTrack> tracks = [];
        if (loaded && active is not null) {
            for (var i = 0; i < active.Item.AudioTracks.Count; i++) {
                var track = active.Item.AudioTracks[i];
                tracks.Add(new(i, "audio", track.Label, track.Language, active.Item.AudioTracks.SelectedIndex == i));
            }
            for (var i = 0; i < active.Item.TimedMetadataTracks.Count; i++) {
                var track = active.Item.TimedMetadataTracks[i];
                if (!IsSubtitle(track)) continue;
                tracks.Add(new(i, "subtitle", track.Label, track.Language,
                    active.Item.TimedMetadataTracks.GetPresentationMode((uint)i) == TimedMetadataTrackPresentationMode.PlatformPresented,
                    active.ExternalFiles.GetValueOrDefault(track)?.OriginalPath));
            }
        }
        return new(generation, sequence, phase, path, loaded,
            ended || session?.PlaybackState != MediaPlaybackState.Playing, seeking,
            session?.CanSeek == true && session.NaturalDuration > TimeSpan.Zero,
            active is null ? 0 : MoviePosition(active), active is null ? 0 : MovieDuration(active),
            (active?.SourceOwner as WindowsContinuousSource)?.PlaybackRate ?? session?.PlaybackRate ?? rate, active?.Player.Volume ?? volume,
            session?.NaturalVideoWidth ?? 0, session?.NaturalVideoHeight ?? 0, error, tracks.ToArray(),
            active?.Player.IsMuted ?? muted, loop, repeatStart, repeatEnd,
            canAdjustSoftwareSubtitle ? active!.SubtitleDelay : plainSubtitle?.Delay ?? 0,
            active?.Player.IsVideoFrameServerEnabled == true ? lastRotation : (int)(session?.PlaybackRotation ?? MediaRotation.None) * 90,
            active is not null ? active.Player.AudioDevice?.Id ?? "" : audioDevice?.Id ?? "",
            active?.Player.AudioBalance ?? audioBalance,
            CurrentSubtitleStyle()?.Applied, CurrentSubtitleStyle() is not null,
            active?.Decoder ?? decoder, isNetwork,
            isNetwork && path.Length > 0 && pendingOpen is null,
            usedSoftwareFallback, recoveryMessage, active?.AudioDelay ?? 0,
            loaded && active?.SoftwareSource?.SetAudioDelay is not null && active.Item.AudioTracks.Count > 0,
            canAdjustSoftwareSubtitle || plainSubtitle is not null,
            loaded && active is not null && (active.SoftwareSource?.AddSubtitleAsync is not null ||
                active.SoftwareSource is null && (!isNetwork || session?.CanSeek == true && session.NaturalDuration > TimeSpan.Zero)),
            active?.SoftwareSource is not null, SubtitleStyleNotice());
    }
    private void Publish() { sequence++; StateChanged?.Invoke(Capture()); }
    private void ReleaseActive()
    {
        var old = active;
        active = null;
        void Release(Action action, string resource)
        {
            try { action(); }
            catch (Exception exception) {
                Diagnostic?.Invoke($"video renderer cleanup: generation={old?.Generation}; resource={resource}; hresult=0x{exception.HResult:X8}; error={exception.Message}");
            }
        }
        Release(() => PlayerChanged?.Invoke(null), "surface");
        if (old is null) return;
        Release(old.Lifetime.Cancel, "cancellation");
        old.Ready.TrySetException(new OperationCanceledException("Video load closed."));
        foreach (var ready in old.PendingSubtitles) ready.TrySetException(new OperationCanceledException("Subtitle load closed."));
        foreach (var unsubscribe in old.Unsubscribe) Release(unsubscribe, "events");
        Release(old.Player.Pause, "pause");
        Release(() => old.Player.Source = null, "source detach");
        Release(old.Player.Dispose, "player");
        if (old.SourceOwner is not null) Release(old.SourceOwner.Dispose, "decoder");
        else Release(old.Source.Dispose, "source");

        foreach (var stream in old.SubtitleStreams) Release(stream.Dispose, "subtitle");
        old.Lifetime.Dispose();
    }
    public void Stop()
    {
        CheckThread();
        generation++;
        CancelPendingOpen();
        ReleaseActive();
        loaded = seeking = ended = false;
        isNetwork = usedSoftwareFallback = false;
        desiredPaused = true;
        lastPosition = 0;
        lastRotation = 0;
        repeatStart = repeatEnd = null;
        path = error = "";
        recoveryMessage = "";
        phase = "idle";
        decoder = SystemDecoder;
        Publish();
    }
    public void Dispose()
    {
        if (disposed) return;
        CheckThread();
        Stop();
        disposed = true;
    }
}
