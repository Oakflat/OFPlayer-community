using System.Text.Json;

namespace OFPlayer_WinUI.ViewModels;
public sealed record MissingTrackCandidate(string TrackId, string Title, string SourcePath, int PlaylistRelationCount);
public sealed record MissingTracksReview(string LibraryId, string ScanId, IReadOnlyList<MissingTrackCandidate> Tracks, int? AffectedPlaylistCount, int MissingTotal)
{
    public int RelationCount => Tracks.Sum(track => track.PlaylistRelationCount);
    public bool IsTruncated => MissingTotal > Tracks.Count;
}

public sealed partial class MainViewModel
{
    private MissingTracksReview? pendingMissingTracksReview;
    private bool isMissingCleanupRunning;
    public MissingTracksReview? PendingMissingTracksReview { get => pendingMissingTracksReview; private set => SetProperty(ref pendingMissingTracksReview, value); }
    public bool IsMissingCleanupRunning { get => isMissingCleanupRunning; private set => SetProperty(ref isMissingCleanupRunning, value); }

    private void SetMissingTracksReviewFromScan(JsonElement result, string libraryId)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("missingScanId", out var scanIdElement) || scanIdElement.ValueKind != JsonValueKind.String || !result.TryGetProperty("missingTracks", out var tracksElement) || tracksElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var scanId = scanIdElement.GetString();
        if (string.IsNullOrWhiteSpace(scanId))
        {
            return;
        }

        var candidates = new List<MissingTrackCandidate>();
        foreach (var item in tracksElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("trackId", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var title = item.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String ? titleElement.GetString() ?? string.Empty : string.Empty;
            var path = item.TryGetProperty("sourcePath", out var pathElement) && pathElement.ValueKind == JsonValueKind.String ? pathElement.GetString() ?? string.Empty : string.Empty;
            var relations = item.TryGetProperty("playlistRelationCount", out var countElement) && countElement.ValueKind == JsonValueKind.Number ? countElement.GetInt32() : 0;
            candidates.Add(new MissingTrackCandidate(id, title, path, Math.Max(0, relations)));
        }

        if (candidates.Count == 0)
        {
            return;
        }

        int? affectedPlaylists = result.TryGetProperty("affectedPlaylistCount", out var playlistCount) && playlistCount.ValueKind == JsonValueKind.Number ? Math.Max(0, playlistCount.GetInt32()) : null;
        var missingTotal = result.TryGetProperty("missingTotal", out var totalElement) && totalElement.ValueKind == JsonValueKind.Number ? Math.Max(candidates.Count, totalElement.GetInt32()) : candidates.Count;
        var review = new MissingTracksReview(libraryId, scanId, candidates, affectedPlaylists, missingTotal);
        post(() => PendingMissingTracksReview = review);
    }

    public void KeepMissingTracks(MissingTracksReview? expected = null)
    {
        // Keeping records is the default; this only dismisses the prompt.
        if (expected is null || ReferenceEquals(PendingMissingTracksReview, expected))
        {
            PendingMissingTracksReview = null;
        }
    }

    public async Task ConfirmMissingTracksCleanupAsync(MissingTracksReview review)
    {
        if (client is null || closing || !ReferenceEquals(PendingMissingTracksReview, review))
        {
            return;
        }

        if (IsRemoteMode || ActiveLibraryId != review.LibraryId)
        {
            post(() =>
            {
                StatusKind = StatusKind.Warning;
                StatusMessage = Services.Localization.Get("missing.switchBack");
            });
            return;
        }

        if (Interlocked.CompareExchange(ref importOperationGate, 1, 0) != 0)
        {
            post(() =>
            {
                StatusKind = StatusKind.Warning;
                StatusMessage = Services.Localization.Get("missing.busy");
            });
            return;
        }

        var activeClient = client;
        post(() => IsMissingCleanupRunning = true);
        try
        {
            // The host's one-shot scan snapshot and filesystem recheck are the
            // only authority for deletion. Never route this through generic delete.
            var result = await activeClient.SendAsync("desktop_library_confirm_missing_cleanup", new { libraryId = review.LibraryId, missingScanId = review.ScanId, trackIds = review.Tracks.Select(track => track.TrackId).ToArray(), }, CancellationToken.None);
            ApplyCommandResult(result);
            var deleted = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("deletedTrackIds", out var deletedElement) && deletedElement.ValueKind == JsonValueKind.Array ? deletedElement.GetArrayLength() : 0;
            var skipped = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("skippedTrackIds", out var skippedElement) && skippedElement.ValueKind == JsonValueKind.Array ? skippedElement.GetArrayLength() : 0;
            post(() =>
            {
                if (ReferenceEquals(PendingMissingTracksReview, review))
                {
                    PendingMissingTracksReview = null;
                }
            });
            await RefreshAfterMutationAsync(skipped > 0 ? Services.Localization.Format("missing.done.kept", ("count", deleted.ToString()), ("kept", skipped.ToString())) : Services.Localization.Format(review.IsTruncated ? "missing.done.partial" : "missing.done", ("count", deleted.ToString())));
        }
        catch (Exception error)
        {
            // The scan token might have expired or been consumed. Require a fresh
            // scan before any further cleanup attempt.
            post(() =>
            {
                if (ReferenceEquals(PendingMissingTracksReview, review))
                {
                    PendingMissingTracksReview = null;
                }
            });
            ReportCommandError(error);
        }
        finally
        {
            post(() =>
            {
                IsMissingCleanupRunning = false;
                Interlocked.Exchange(ref importOperationGate, 0);
            });
        }
    }
}
