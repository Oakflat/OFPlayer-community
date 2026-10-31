using OFPlayer.WinUI.Core.Playback;
using OFPlayer_WinUI.Models;

namespace OFPlayer_WinUI.ViewModels;
public sealed partial class MainViewModel
{
    public sealed record TrackReorderContext(string LibraryId, string CollectionRef, long Epoch, string Sort, IReadOnlyList<string> VisibleIds, IReadOnlyList<string> QueueIds, string RoomId, string RoomEpoch);
    private bool trackDragActive;
    private bool savingTrackOrder;
    private string? playbackCollectionRef;
    private string? playbackLibraryId;
    public bool IsSavingTrackOrder => savingTrackOrder;
    public bool CanReorderTracks => IsConnected && !IsLoadingPage && !savingTrackOrder && !editingQueue && !IsInsideGroup && !IsGroupListVisible && (ActiveCollectionRef == "view:current-queue" || playlists.Any(p => $"playlist:{p.Id}" == ActiveCollectionRef && (p.Kind == "user" || p.SystemKey == "all-tracks")));

    public TrackReorderContext? BeginTrackReorder()
    {
        if (!CanReorderTracks || ActiveCollectionRef is null)
            return null;
        trackDragActive = true;
        OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Mark("catalog.reorder.begin", new { ActiveCollectionRef, visibleCount = Tracks.Count });
        return new(ActiveLibraryId, ActiveCollectionRef, pageEpoch, SortOption, Tracks.Select(row => row.Id).ToArray(), PresentedQueueIds.ToArray(), "", "");
    }

    public void RenumberTracks()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.TrackOrder.RenumberTracks");
        for (var index = 0; index < Tracks.Count; index++)
            Tracks[index].RowNumber = TrackWindowOffset + index + 1;
    }

    public void CancelTrackReorder()
    {
        trackDragActive = false;
    }

    public async Task CompleteTrackReorderAsync(TrackReorderContext? context, bool moved)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.TrackOrder.CompleteTrackReorderAsync");
        trackDragActive = false;
        if (context is null || client is null || context.Epoch != pageEpoch || context.CollectionRef != ActiveCollectionRef || context.LibraryId != ActiveLibraryId)
            return;
        if (context.RoomId != ("") || context.RoomEpoch != (""))
        {
            return;
        }

        var after = Tracks.Select(row => row.Id).ToArray();
        RenumberTracks();
        if (context.VisibleIds.SequenceEqual(after))
            return;
        if (!moved)
        {
            await LoadFirstPageAsync();
            return;
        }

        savingTrackOrder = true;
        RaisePropertyChanged(nameof(IsSavingTrackOrder));
        using var operation = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("catalog.reorder.commit");
        try
        {
            var queueView = context.CollectionRef == "view:current-queue";
            // Query the whole collection, even when the UI is filtered/paged.
            // Only the displayed slots move; hidden rows keep their relative order.
            var fullOrder = queueView ? context.QueueIds : TrackQueryPage.FromJson(await client.SendAsync("desktop_catalog_query_collection_tracks", new { activeLibrary = context.LibraryId, activeCollectionRef = context.CollectionRef, searchQuery = "", typeFilter = "", sortOption = context.Sort, offset = 0, limit = 1, includeTrackIds = true, }, CancellationToken.None)).TrackIds;
            if (context.Epoch != pageEpoch || context.CollectionRef != ActiveCollectionRef)
                return;
            if (context.RoomId != ("") || context.RoomEpoch != (""))
                throw new InvalidOperationException(Services.Localization.Get("order.queueChanged"));
            if (queueView && !context.QueueIds.SequenceEqual(PresentedQueueIds))
                throw new InvalidOperationException(Services.Localization.Get("order.queueChanged"));
            var ordered = TrackOrder.Merge(fullOrder, context.VisibleIds, after);
            if (queueView)
            {
                ApplyCommandResult(await client.SendAsync("playback_session_set_queue", new { trackIds = ordered }, CancellationToken.None));
            }
            else
            {
                await client.SendAsync("desktop_playlist_reorder_tracks", new { playlistId = context.CollectionRef["playlist:".Length..], orderedTrackIds = ordered, }, CancellationToken.None);
                if (context.RoomId != ("") || context.RoomEpoch != (""))
                    return;
                // Editing the source of the current queue updates its remaining
                // order without seeking, reloading audio, or changing the track.
                if (playbackCollectionRef == context.CollectionRef && playbackLibraryId == context.LibraryId && context.QueueIds.SequenceEqual(PresentedQueueIds))
                {
                    {
                        var queueSet = currentQueueTrackIds.ToHashSet(StringComparer.Ordinal);
                        var nextQueue = ordered.Where(queueSet.Contains).ToArray();
                        if (nextQueue.Length == currentQueueTrackIds.Count)
                            ApplyCommandResult(await client.SendAsync("playback_session_set_queue", new { trackIds = nextQueue }, CancellationToken.None));
                    }
                }
            }

            await client.SendAsync("desktop_preferences_save", new { patch = collectionSorting.Set(context.CollectionRef, "manual") }, CancellationToken.None);
            if (context.CollectionRef == ActiveCollectionRef && context.LibraryId == ActiveLibraryId)
            {
                SortOption = "manual";
                currentCollectionTrackIds = ordered;
                StatusKind = StatusKind.Success;
                StatusMessage = Services.Localization.Get("done.order.saved");
                await LoadFirstPageAsync();
            }

            OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Mark("catalog.reorder.saved", new { context.CollectionRef, count = ordered.Count });
        }
        catch (Exception error)
        {
            ReportCommandError(error);
            // Roll back a failed optimistic move to the authoritative saved order.
            if (context.CollectionRef == ActiveCollectionRef && context.LibraryId == ActiveLibraryId)
                await LoadFirstPageAsync();
        }
        finally
        {
            savingTrackOrder = false;
            RaisePropertyChanged(nameof(IsSavingTrackOrder));
        }
    }
}
