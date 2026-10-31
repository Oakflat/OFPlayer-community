using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OFPlayer.WinUI.Core;
using OFPlayer.WinUI.Core.Protocol;
using OFPlayer.WinUI.Core.Playback;
using OFPlayer_WinUI.Models;
using OFPlayer_WinUI.Services;

namespace OFPlayer_WinUI.ViewModels;
public enum StatusKind
{
    Informational,
    Warning,
    Success,
    Error,
}

/// <summary>
/// Main-window view model (W2). All host communication, navigation and playback
/// decisions live here; the window code-behind only maps properties to controls and
/// forwards input events. The Rust host owns catalog/queue/playback facts - this
/// class keeps display pages only (never a second catalog or playback engine).
/// Catalog navigation/paging lives in MainViewModel.Catalog.cs.
/// </summary>
public sealed partial class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly Action<Action> post;
    private NativeHostService? hostService;
    private NativeHostClient? client;
    private CancellationTokenSource? lifetime;
    private bool closing;
    private string hostVersion = string.Empty;
    /// <summary>Internal host version reported by the handshake; not the client release version.</summary>
    public string HostVersionText => hostVersion;
    /// <summary>N3: the single live view model (used by the immersive window bridge).</summary>
    internal static MainViewModel? Active { get; private set; }
    /// <summary>N3: bridge access to the single host client (allowlisted consumers only).</summary>
    internal NativeHostClient? HostClientForBridge => client;

    private PlaybackStateSnapshot playback = PlaybackStateSnapshot.Idle;
    private readonly OFPlayer.WinUI.Core.Playback.PlaybackStateOrder playbackStateOrder = new();
    private readonly QueueObservationOrder queueObservationOrder = new();
    private IReadOnlyList<string> currentQueueTrackIds = Array.Empty<string>();
    private bool isBusy;
    private bool isConnected;
    private string statusMessage = Localization.Get("status.starting");
    private StatusKind statusKind = StatusKind.Informational;
    private string connectButtonText = Localization.Get("common.retry");
    public MainViewModel(Action<Action> post)
    {
        this.post = post;
        Active = this;
        FailureText.Reported += ShowReportedFailure;
        Tracks.CollectionChanged += (_, _) => RaisePropertyChanged(nameof(LoadedCount));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Raised after the navigation tree data changed; the shell rebuilds its menu.</summary>
    public event Action? NavigationChanged;
    public ObservableCollection<TrackQueryRow> Tracks { get; } = new();

    public bool IsConnected
    {
        get => isConnected;
        private set
        {
            SetProperty(ref isConnected, value);
        }
    }

    public bool IsBusy { get => isBusy; private set => SetProperty(ref isBusy, value); }
    public string PageTitle { get => pageTitle; private set => SetProperty(ref pageTitle, value); }

    public string StatusMessage
    {
        get => statusMessage;
        private set
        {
            StatusDetail = string.Empty;
            StatusFailureTitle = string.Empty;
            SetProperty(ref statusMessage, value);
        }
    }

    private string statusDetail = string.Empty;
    private string statusFailureTitle = string.Empty;
    /// <summary>Operation, error code, reference and cause for the current failure.</summary>
    public string StatusDetail { get => statusDetail; private set => SetProperty(ref statusDetail, value); }
    public string StatusFailureTitle { get => statusFailureTitle; private set => SetProperty(ref statusFailureTitle, value); }
    public StatusKind StatusKind { get => statusKind; private set => SetProperty(ref statusKind, value); }
    public string ConnectButtonText { get => connectButtonText; private set => SetProperty(ref connectButtonText, value); }
    public PlaybackStateSnapshot Playback => playback;
    /// <summary>Detail-first label so the current track survives page changes.</summary>
    public string PlaybackLabel => BuildPlaybackLabel();
    public string DataRootText => hostService is null ? string.Empty : Localization.Format("settings.storage.dataRoot", ("path", hostService.DataRoot));

    public async Task ConnectAsync()
    {
        if (closing || IsConnected || IsBusy)
            return;
        IsBusy = true;
        try
        {
            lifetime?.Cancel();
            if (client is not null)
                await client.DisposeAsync();
            lifetime?.Dispose();
            lifetime = new();
            hostService = new NativeHostService();
            client = await hostService.ConnectAsync(lifetime.Token);
            playbackStateOrder.Reset();
            queueObservationOrder.Reset();
            _ = RunEventLoopAsync(client, lifetime.Token);
            IsConnected = true;
            StatusMessage = "";
            hostVersion = client.Handshake?.HostVersion ?? "";
            await LoadStructureAsync();
            await RestoreSessionAsync();
            await LoadPreferencesAsync();
            await RefreshOutputDevicesAsync();
            ApplyPreferredVolumeToEngine();
            _ = LoadListeningStatsAsync();
            StartDailyActivity(hostService.DataRoot);
            SyncStorageWatchAsync();
            ScheduleStartupScan();
        }
        catch (Exception error)
        {
            ReportCommandError(error);
            IsConnected = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Restores the persisted queue/position without audible autoplay, matching the
    /// Tauri startup behaviour (`playback_session_sync_catalog` loads paused).
    /// </summary>
    private async Task RestoreSessionAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.RestoreSessionAsync");
        if (client is null)
        {
            return;
        }

        var result = await client.SendAsync("playback_session_sync_catalog", null, CancellationToken.None);
        ApplyCommandResult(result);
    }

    /// <summary>
    /// Clicking a row plays it within the CURRENT FILTERED COLLECTION queue
    /// (visible ids cached by the page query), matching the old front-end
    /// (handleSelectTrack emits visibleTrackIds as the queue).
    /// </summary>
    public async Task PlayTrackAsync(TrackQueryRow row)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.PlayTrackAsync");
        if (client is null || IsLoadingPage || savingTrackOrder || trackDragActive)
        {
            return;
        }

        var sourceCollection = ActiveCollectionRef;
        var sourceLibrary = ActiveLibraryId;
        IReadOnlyList<string> queue = sourceCollection == "view:current-queue" ? currentQueueTrackIds : currentCollectionTrackIds.Count > 0 ? currentCollectionTrackIds : new[]
        {
            row.Id
        };
        try
        {
            if (await OfferMissingFileRecoveryAsync(row))
                return;
            var result = await client.SendAsync("playback_session_select_track", new { trackId = row.Id, queueTrackIds = queue, autoplay = true }, CancellationToken.None);
            if (sourceCollection != "view:current-queue")
            {
                playbackCollectionRef = sourceCollection;
                playbackLibraryId = sourceLibrary;
            }

            ApplyCommandResult(result);
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    /// <summary>
    /// Plays the given rows as the queue (first row selected). Queue ownership stays
    /// in the Rust session; the view model only forwards ids.
    /// </summary>
    public async Task PlayRowsAsync(IReadOnlyList<TrackQueryRow> rows)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.PlayRowsAsync");
        if (client is null || rows.Count == 0)
        {
            return;
        }

        var trackIds = rows.Select(row => row.Id).ToArray();
        var sourceCollection = ActiveCollectionRef;
        var sourceLibrary = ActiveLibraryId;
        try
        {
            var result = await client.SendAsync("playback_session_select_track", new { trackId = trackIds[0], queueTrackIds = trackIds, autoplay = true }, CancellationToken.None);
            if (sourceCollection != "view:current-queue")
            {
                playbackCollectionRef = sourceCollection;
                playbackLibraryId = sourceLibrary;
            }

            ApplyCommandResult(result);
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    /// <summary>Plays the whole filtered view (uses the full id list returned by the query).</summary>
    public async Task PlayAllAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.PlayAllAsync");
        if (client is null || savingTrackOrder || trackDragActive)
        {
            return;
        }

        var sourceCollection = ActiveCollectionRef;
        var sourceLibrary = ActiveLibraryId;
        try
        {
            var idsResult = activeGroupKey is null ? await client.SendAsync("desktop_catalog_query_collection_tracks", new { activeLibrary = ActiveLibraryId, activeCollectionRef = ActiveCollectionRef, searchQuery = searchText, typeFilter = typeFilter, sortOption = sortOption, offset = 0, limit = 1, includeTrackIds = true, }, CancellationToken.None) : await client.SendAsync("desktop_catalog_query_group_tracks", new { groupKind = activeGroupKind, groupKey = activeGroupKey, activeLibrary = ActiveLibraryId, searchQuery = searchText, typeFilter = typeFilter, sortOption = sortOption, offset = 0, limit = 1, includeTrackIds = true, }, CancellationToken.None);
            var page = TrackQueryPage.FromJson(idsResult);
            if (page.TrackIds.Count == 0)
            {
                return;
            };
            var result = await client.SendAsync("playback_session_select_track", new { trackId = page.TrackIds[0], queueTrackIds = page.TrackIds, autoplay = true }, CancellationToken.None);
            if (sourceCollection != "view:current-queue")
            {
                playbackCollectionRef = sourceCollection;
                playbackLibraryId = sourceLibrary;
            }

            ApplyCommandResult(result);
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        closing = true;
        FailureText.Reported -= ShowReportedFailure;
        searchDebounce?.Cancel();
        pageRequest?.Cancel();
        volumeSaveDebounce?.Cancel();
        lifetime?.Cancel();
        if (activity is not null)
            await activity.DisposeAsync();
        if (client is not null)
            await client.DisposeAsync();
        searchDebounce?.Dispose();
        pageRequest?.Dispose();
        volumeSaveDebounce?.Dispose();
        lifetime?.Dispose();
        Active = null;
    }

    private async Task RunEventLoopAsync(NativeHostClient activeClient, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var e in activeClient.ReadEventsAsync(cancellationToken))
            {
                if (closing || activeClient != client)
                    return;
                if (e.Name == "playback.state")
                {
                    ApplyCommandResult(e.Data);
                    if (e.Data.TryGetProperty("playback", out var snapshot) && snapshot.TryGetProperty("audioLevels", out var levels))
                        PlaybackEventRelay.PublishMeter(JsonSerializer.SerializeToElement(new { levels }), e.Sequence);
                }
                else if (e.Name == "storage.watch-changed")
                    post(() => HandleStorageWatchEvent(e.Data));
                else if (e.Name == "import.progress")
                    ApplyImportProgress(e.Data);
            }
        }
        catch (OperationCanceledException)when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            post(() =>
            {
                if (!closing && activeClient == client)
                {
                    IsConnected = false;
                    ReportCommandError(error);
                }
            });
        }
    }

    /// <summary>
    /// Mirrors the old front-end end-of-track flow: the event is only a trigger;
    /// the next id is resolved with reason=ended (repeat=one replays,
    /// repeat=none at the tail pauses) and selected on the host.
    /// </summary>
    private Task HandleEndedAsync(string? endedTrack)
    {
        if (endedTrack is not null && endedTrack != playback.ActiveTrackId)
            return Task.CompletedTask;
        return PlaySkipAsync(1, OFPlayer.WinUI.Core.Playback.PlaybackOrder.ReasonEnded);
    }

    private void ApplyCommandResult(JsonElement result)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.ApplyCommandResult");
        var resultClient = client;
        PlaybackStateSnapshot? snapshot = null;
        SessionQueueInfo? sessionInfo = null;
        if (result.ValueKind == JsonValueKind.Object)
        {
            if (result.TryGetProperty("playback", out var playbackElement))
            {
                snapshot = PlaybackStateSnapshot.FromJson(playbackElement);
            }
            else if (result.TryGetProperty("status", out _) && result.TryGetProperty("volume", out _))
            {
                // Volume, seek and output commands return a PlaybackSnapshot directly.
                snapshot = PlaybackStateSnapshot.FromJson(result);
            }

            if (result.TryGetProperty("session", out var sessionElement) && sessionElement.ValueKind == JsonValueKind.Object)
            {
                sessionInfo = SessionQueueInfo.FromJson(sessionElement);
            }
            else if (result.TryGetProperty("queueTrackIds", out _))
            {
                sessionInfo = SessionQueueInfo.FromJson(result);
            }
        }

        post(() =>
        {
            if (closing || resultClient != client)
                return;
            // Playback and queue revisions are independent: queue-only edits do
            // not seek/reload audio, and late playback receipts cannot undo them.
            if (sessionInfo is not null)
                ApplySessionQueue(sessionInfo);
            if (snapshot is not null)
            {
                ApplyPlaybackSnapshot(snapshot);
            }
        });
    }

    private void ApplyPlaybackSnapshot(PlaybackStateSnapshot snapshot)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.ApplyPlaybackSnapshot");
        if (!playbackStateOrder.TryAcceptSnapshot(snapshot.Revision, snapshot.ActiveTrackId))
            return;
        var previousTrackId = playback.ActiveTrackId;
        playback = snapshot;
        if (snapshot.ActiveTrackId != previousTrackId)
            RaisePropertyChanged(nameof(CanEditLocalQueue));
        if (IsPlaying)
            activity?.Record();
        RaisePropertyChanged(nameof(Playback));
        RaisePropertyChanged(nameof(PlaybackLabel));
        RaisePropertyChanged(nameof(IsPlaying));
        RaisePropertyChanged(nameof(CanPlay));
        RaisePropertyChanged(nameof(CanSkip));
        RaisePropertyChanged(nameof(IsPlaybackBuffering));
        // D4: external playback (id "external") has no library detail to load, and
        // returning to library playback clears the remembered remote label.
        var externalActive = string.Equals(snapshot.ActiveTrackId, "external", StringComparison.Ordinal) || snapshot.ActiveTrackId?.StartsWith("remote/", StringComparison.Ordinal) == true;
        if (!externalActive && snapshot.ActiveTrackId is not null && snapshot.ActiveTrackId != previousTrackId)
        {
            externalPlaybackLabel = string.Empty;
            _ = LoadCurrentTrackDetailAsync(snapshot.ActiveTrackId);
            if (IsLyricsVisible)
            {
                _ = ReloadLyricsAsync(snapshot.PositionSeconds);
            }
        }

        UpdateActiveLyricLine(snapshot.PositionSeconds);
        UpdateActiveRow(snapshot.ActiveTrackId);
        PublishWebPlayback();
    }

    private void PublishWebPlayback()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.PublishWebPlayback");
        // Publish only after the UI-owned snapshot has been applied. Progress
        // frames need the track identity and status retained by that snapshot.
        PlaybackEventRelay.Publish(JsonSerializer.SerializeToElement(new { status = playback.Status, activeTrackId = playback.ActiveTrackId, currentTime = Playback.PositionSeconds, duration = playback.DurationSeconds, volume = playback.Volume, }));
    }

    private void ShowReportedFailure(FailureReport failure)
    {
        if (closing || failure.ReasonKey == FailureReason.Cancelled)
            return;
        post(() =>
        {
            if (closing)
                return;
            StatusKind = StatusKind.Error;
            StatusMessage = FailureText.Summary(failure);
            StatusFailureTitle = FailureText.Operation(failure);
            StatusDetail = failure.Details;
        });
    }

    private void ReportCommandError(Exception error, [CallerMemberName] string operation = "", Func<bool>? isCurrent = null)
    {
        if (closing || isCurrent?.Invoke() == false)
            return;
        var failure = FailureText.Capture(error, operation, notify: false);
        if (failure.ReasonKey == FailureReason.Cancelled)
            return;
        post(() =>
        {
            if (closing || isCurrent?.Invoke() == false)
                return;
            StatusKind = StatusKind.Error;
            StatusMessage = FailureText.Summary(failure);
            StatusFailureTitle = FailureText.Operation(failure);
            StatusDetail = failure.Details;
        });
    }

    private TrackQueryRow? FindLoadedTrack(string? trackId) => trackId is null ? null : Tracks.FirstOrDefault(row => row.Id == trackId);
    private void RaisePropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        RaisePropertyChanged(propertyName);
    }
}
