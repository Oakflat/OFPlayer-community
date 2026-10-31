using System.Text.Json;
using OFPlayer.WinUI.Core;
using OFPlayer.WinUI.Core.Playback;
using OFPlayer_WinUI.Services;

namespace OFPlayer_WinUI.ViewModels;
public sealed partial class MainViewModel
{
    private bool editingQueue;
    private Task queueEditCompletion = Task.CompletedTask;
    public bool IsEditingQueue => editingQueue;
    public bool CanEditLocalQueue => IsConnected && !IsBusy && !editingQueue && !savingTrackOrder && !trackDragActive && !IsRemoteMode && queueObservationOrder.Revision is not null && client is { PlaybackOwnershipRetiring: false } && playback.ActiveTrackId != "external";

    public bool CanEditQueueTracks(IEnumerable<string> ids) => CanEditLocalQueue && ids.Any(id => id != playback.ActiveTrackId);
    public Task PlayTracksNextAsync(IReadOnlyList<string> ids) => EditLocalQueueAsync("next", ids);
    public Task AppendTracksToQueueAsync(IReadOnlyList<string> ids) => EditLocalQueueAsync("append", ids);
    public Task RemoveTracksFromQueueAsync(IReadOnlyList<string> ids) => EditLocalQueueAsync("remove", ids);
    // Must run on the UI thread. No PlaybackStateSnapshot is applied here.
    private bool ApplySessionQueue(SessionQueueInfo info)
    {
        if (!queueObservationOrder.TryAccept(info.Revision, info.QueueTrackIds))
            return false;
        var changed = !currentQueueTrackIds.SequenceEqual(info.QueueTrackIds);
        currentQueueTrackIds = info.QueueTrackIds;
        QueueTrackCount = info.QueueTrackIds.Count;
        RaisePropertyChanged(nameof(CanEditLocalQueue));
        if (changed)
        {
            _ = RefreshNavigationAsync();
            if (ActiveCollectionRef == "view:current-queue" && !savingTrackOrder && !trackDragActive)
                _ = LoadFirstPageAsync();
        }

        return true;
    }

    private async Task EditLocalQueueAsync(string action, IReadOnlyList<string> selected)
    {
        if (!CanEditLocalQueue || client is not { } activeClient || queueObservationOrder.Revision is not ulong revision)
            return;
        if (action == "remove" && ActiveCollectionRef != "view:current-queue")
            return;
        var ids = PlaybackOrder.Normalize(selected);
        if (ids.Count == 0)
            return;
        var anchor = playback.ActiveTrackId;
        // Capture the existing shuffle before any reply/event can refresh membership.
        var order = GetPlaybackQueueOrder();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queueEditCompletion = completion.Task;
        editingQueue = true;
        RaisePropertyChanged(nameof(IsEditingQueue));
        try
        {
            var result = await activeClient.SendAsync("playback_session_edit_queue", new { action, trackIds = ids, expectedQueueRevision = revision, expectedTrackId = anchor, }, CancellationToken.None);
            var info = SessionQueueInfo.FromJson(result.GetProperty("session"));
            var edited = result.GetProperty("editedTrackIds").EnumerateArray().Select(id => id.GetString()!).ToArray();
            await ApplyQueueOnUiAsync(activeClient, () =>
            {
                if (!ApplySessionQueue(info))
                    return;
                if (shuffleEnabled)
                    shuffledOrder = QueueObservationOrder.ApplyEdit(order, info.QueueTrackIds, action, edited, info.CurrentTrackId);
                // Manual edits detach the queue from playlist drag-following.
                playbackCollectionRef = playbackLibraryId = null;
                StatusKind = StatusKind.Success;
                StatusMessage = Localization.Format("queue.done." + action, ("count", edited.Length.ToString(Localization.Culture)));
                if (ids.Contains(info.CurrentTrackId ?? ""))
                    StatusMessage += " " + Localization.Get("queue.currentKept");
            });
        }
        catch (Exception error)
        {
            if (client != activeClient || closing)
                return;
            // Lost receipts are unknown outcomes: read authority, never replay a mutation.
            try
            {
                var latest = SessionQueueInfo.FromJson(await activeClient.SendAsync("playback_session_snapshot", null, CancellationToken.None));
                await ApplyQueueOnUiAsync(activeClient, () => ApplySessionQueue(latest));
            }
            catch
            { /* Keep the original failure; reconnect will restore the queue. */
            }

            post(() =>
            {
                if (client != activeClient || closing)
                    return;
                if (error is HostCommandException { Code: "OFPLAYER_QUEUE_CHANGED" })
                {
                    StatusKind = StatusKind.Warning;
                    StatusMessage = Localization.Get("queue.changed");
                }
                else if (error is HostRequestTimeoutException)
                {
                    StatusKind = StatusKind.Warning;
                    StatusMessage = Localization.Get("queue.unknown");
                }
                else
                    ReportCommandError(error);
            });
        }
        finally
        {
            editingQueue = false;
            RaisePropertyChanged(nameof(IsEditingQueue));
            completion.TrySetResult();
        }
    }

    private Task ApplyQueueOnUiAsync(NativeHostClient observedClient, Action apply)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        post(() =>
        {
            try
            {
                if (!closing && client == observedClient)
                    apply();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
            finally
            {
                completion.TrySetResult();
            }
        });
        return completion.Task;
    }
}
