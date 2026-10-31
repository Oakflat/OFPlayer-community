using System.ComponentModel;
using Microsoft.UI.Dispatching;
using OFPlayer_WinUI.ViewModels;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

namespace OFPlayer_WinUI.Services;
/// <summary>Window-owned SMTC bridge; the native host remains the only audio player.</summary>
internal sealed class SystemMediaSession : IDisposable
{
    private readonly MainViewModel model;
    private readonly DispatcherQueue dispatcher;
    private readonly SystemMediaTransportControls controls;
    private bool disposed;
    private bool updateQueued;
    private string? metadataTrackId;
    private string? metadataTitle;
    private string? metadataArtist;
    private string? metadataAlbum;
    private string? metadataArtwork;
    private int artworkGeneration;
    private IRandomAccessStream? artworkStream;
    private long lastTimelineAt;
    private string lastStatus = string.Empty;
    private double lastPosition;
    private bool timelinePublished;
    public SystemMediaSession(nint hwnd, DispatcherQueue dispatcher, MainViewModel model)
    {
        this.model = model;
        this.dispatcher = dispatcher;
        controls = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        controls.ButtonPressed += ButtonPressed;
        controls.PlaybackPositionChangeRequested += PositionRequested;
        model.PropertyChanged += ModelChanged;
        Update();
        PreviewLog.Write("system media: window session attached");
    }

    private static bool TracksProperty(string? name) => name is nameof(MainViewModel.Playback) or nameof(MainViewModel.CurrentTrackDetail) or nameof(MainViewModel.PlaybackTitle) or nameof(MainViewModel.PlaybackArtwork) or nameof(MainViewModel.PlaybackArtist) or nameof(MainViewModel.IsConnected);
    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!TracksProperty(args.PropertyName))
            return;
        if (disposed)
            return;
        // The first update used TryEnqueue; a saturated dispatcher queue would drop it
        // and leave the session stale forever. Update directly when already on the UI
        // thread, and keep one queued update as the coalescing fallback.
        if (dispatcher.HasThreadAccess)
        {
            Update();
            return;
        }

        if (updateQueued)
            return;
        updateQueued = true;
        if (!dispatcher.TryEnqueue(() =>
        {
            updateQueued = false;
            if (!disposed)
                Update();
        }))
        {
            // Session state is now stale, but dropping a queued retry is safer than
            // touching SMTC from a non-UI thread.
            updateQueued = false;
            PreviewLog.Write("system media: update dispatch rejected");
        }
    }

    private void Update()
    {
        var started = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.NowMs;
        try
        {
            var playback = model.Playback;
            var available = model.IsConnected && playback.ActiveTrackId is not null;
            // Publish the session only while a track is actually available. Traffic
            // must be enabled before metadata so Windows surfaces the session in the
            // media flyout; enabling it after the updater runs leaves it unpublished.
            controls.IsEnabled = available;
            controls.IsPlayEnabled = available;
            controls.IsPauseEnabled = available;
            controls.IsNextEnabled = available;
            controls.IsPreviousEnabled = available;
            controls.PlaybackStatus = !available ? MediaPlaybackStatus.Closed : playback.Playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            var detail = model.CurrentTrackDetail;
            if (detail?.Id != playback.ActiveTrackId)
                detail = null;
            var title = available ? model.PlaybackTitle : string.Empty;
            if (metadataTrackId != playback.ActiveTrackId || metadataTitle != title || metadataArtist != model.PlaybackArtist || metadataAlbum != detail?.Album || metadataArtwork != model.PlaybackArtwork)
            {
                metadataTrackId = playback.ActiveTrackId;
                metadataTitle = title;
                metadataArtist = model.PlaybackArtist;
                metadataAlbum = detail?.Album;
                metadataArtwork = model.PlaybackArtwork;
                var updater = controls.DisplayUpdater;
                updater.ClearAll();
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = title;
                updater.MusicProperties.Artist = model.PlaybackArtist;
                updater.MusicProperties.AlbumTitle = detail?.Album ?? string.Empty;
                updater.Update();
                // Bump the generation before the await so a slower older load cannot
                // win the Thumbnail write against this newer request.
                var generation = ++artworkGeneration;
                _ = UpdateArtworkAsync(model.PlaybackArtwork, generation);
                // Record the publication attempt: a session that never appears in the
                // system flyout is otherwise indistinguishable from a working one.
                PreviewLog.Write($"system media: publish track={playback.ActiveTrackId} enabled={available} " + $"status={controls.PlaybackStatus} titleLength={title?.Length ?? 0} artist={detail?.Artist is not null}");
            }

            var now = Environment.TickCount64;
            if (timelinePublished && now - lastTimelineAt < 1000 && lastStatus == playback.Status && Math.Abs(playback.PositionSeconds - lastPosition) < 1.5)
                return;
            lastTimelineAt = now;
            lastStatus = playback.Status;
            lastPosition = playback.PositionSeconds;
            timelinePublished = true;
            var duration = Math.Max(0, playback.DurationSeconds);
            controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties { StartTime = TimeSpan.Zero, MinSeekTime = TimeSpan.Zero, Position = TimeSpan.FromSeconds(Math.Clamp(playback.PositionSeconds, 0, duration)), MaxSeekTime = TimeSpan.FromSeconds(duration), EndTime = TimeSpan.FromSeconds(duration), });
        }
        catch (Exception error)
        {
            PreviewLog.Write($"system media update failed: {error.GetType().Name}");
        }
        finally
        {
            OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Activity("smtc.update", started);
        }
    }

    private async Task UpdateArtworkAsync(string? source, int generation)
    {
        IRandomAccessStream? stream = null;
        try
        {
            if (disposed || generation != artworkGeneration)
                return;
            RandomAccessStreamReference? reference = null;
            if (source?.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) == true)
            {
                var separator = source.IndexOf(',');
                if (separator < 0 || !source[..separator].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    return;
                var payload = source[(separator + 1)..];
                var bytes = await Task.Run(() => Convert.FromBase64String(payload));
                if (disposed || generation != artworkGeneration)
                    return;
                stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                }

                reference = RandomAccessStreamReference.CreateFromStream(stream);
            }
            else if (!string.IsNullOrEmpty(source) && Path.IsPathFullyQualified(source))
            {
                var file = await StorageFile.GetFileFromPathAsync(source);
                if (disposed || generation != artworkGeneration)
                    return;
                reference = RandomAccessStreamReference.CreateFromFile(file);
            }

            // Re-check across the awaits that could have run past a track switch or close.
            if (disposed || generation != artworkGeneration)
                return;
            controls.DisplayUpdater.Thumbnail = reference;
            controls.DisplayUpdater.Update();
            artworkStream?.Dispose();
            artworkStream = stream;
            stream = null;
        }
        catch (Exception error)
        {
            PreviewLog.Write($"system media artwork failed: {error.GetType().Name}");
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private void ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        Dispatch(async () =>
        {
            // Hardware keys and the flyout repeat Play/Pause while the transport is
            // already in that state; stay idempotent instead of toggling or replaying.
            var playing = model.Playback.Playing;
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    if (playing)
                        return;
                    await model.SetPlaybackPlayingAsync(true);
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    if (!playing)
                        return;
                    await model.SetPlaybackPlayingAsync(false);
                    break;
                case SystemMediaTransportControlsButton.Next:
                    await model.PlayNextAsync();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    await model.PlayPreviousAsync();
                    break;
            }
        });
    }

    private void PositionRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args) => Dispatch(() => model.SeekAsync(Math.Clamp(args.RequestedPlaybackPosition.TotalSeconds, 0, Math.Max(0, model.Playback.DurationSeconds))));
    private void Dispatch(Func<Task> action)
    {
        // Guard before enqueueing as well as inside: a command arriving after close
        // must not be queued onto a torn-down dispatcher queue.
        if (disposed)
            return;
        if (!dispatcher.TryEnqueue(async () =>
        {
            if (disposed || !model.IsConnected)
                return;
            try
            {
                await action();
            }
            catch (Exception error)
            {
                PreviewLog.Write($"system media command failed: {error.GetType().Name}");
            }
        }))
            PreviewLog.Write("system media: command dispatch rejected");
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        ++artworkGeneration;
        model.PropertyChanged -= ModelChanged;
        controls.ButtonPressed -= ButtonPressed;
        controls.PlaybackPositionChangeRequested -= PositionRequested;
        artworkStream?.Dispose();
        artworkStream = null;
        // Release the session as well as closing it: without a null display updater
        // the window's SMTC entry can outlive the process and leave a stale entry in
        // the system media flyout.
        try
        {
            controls.IsEnabled = false;
            controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            controls.DisplayUpdater.ClearAll();
            controls.DisplayUpdater.Update();
        }
        catch (Exception error)
        {
            PreviewLog.Write($"system media release failed: {error.GetType().Name}");
        }

        PreviewLog.Write("system media: window session detached");
    }
}
