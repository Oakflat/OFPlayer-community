using OFPlayer_WinUI.Models;

namespace OFPlayer_WinUI.ViewModels;
public sealed partial class MainViewModel
{
    private void InvalidateTrackNavigation()
    {
        trackNavigationRevision++;
        RaisePropertyChanged(nameof(TrackNavigationIdentity));
    }

    /// <summary>Fetches at most one five-song marker without changing the displayed window or queue.</summary>
    public async Task<IReadOnlyList<TrackQueryRow>> LoadTrackNavigationPreviewAsync(int startIndex, int count, CancellationToken cancellationToken)
    {
        var activeClient = client;
        if (closing || activeClient is null || IsGroupListVisible || startIndex < 0 || startIndex >= TotalCount || count <= 0)
            return Array.Empty<TrackQueryRow>();
        var identity = TrackNavigationIdentity;
        var library = ActiveLibraryId;
        var collection = ActiveCollectionRef;
        var groupKind = activeGroupKind;
        var groupKey = activeGroupKey;
        var artist = parentArtist;
        var search = SearchText;
        var type = TypeFilter;
        var sort = SortOption;
        var previewCount = Math.Min(5, Math.Min(count, TotalCount - startIndex));
        try
        {
            var result = groupKey is null ? await activeClient.SendAsync("desktop_catalog_query_collection_tracks", new { activeLibrary = library, activeCollectionRef = collection, searchQuery = search, typeFilter = type, sortOption = sort, offset = startIndex, limit = previewCount, includeTrackIds = false, }, cancellationToken) : await activeClient.SendAsync("desktop_catalog_query_group_tracks", new { groupKind, groupKey, artistFilter = artist, activeLibrary = library, searchQuery = search, typeFilter = type, sortOption = sort, offset = startIndex, limit = previewCount, includeTrackIds = false, }, cancellationToken);
            if (cancellationToken.IsCancellationRequested || activeClient != client || identity != TrackNavigationIdentity)
                return Array.Empty<TrackQueryRow>();
            return TrackQueryPage.FromJson(result).Rows.Take(previewCount).ToArray();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<TrackQueryRow>();
        }
        catch (Exception)
        {
            // The preview has its own empty state; a failed hover must not change app status.
            return Array.Empty<TrackQueryRow>();
        }
    }

    /// <summary>
    /// Where the playing song sits in the list on screen, counted over the whole collection rather than
    /// the loaded window; -1 when it is not in this list (or no list of songs is showing).
    /// </summary>
    public int PlayingTrackIndex
    {
        get
        {
            var id = Playback.ActiveTrackId;
            if (id is null || IsGroupListVisible || IsRemoteMode)
                return -1;
            var ids = currentCollectionTrackIds;
            for (var index = 0; index < ids.Count; index++)
                if (string.Equals(ids[index], id, StringComparison.Ordinal))
                    return index;
            return -1;
        }
    }

    /// <summary>Brings the window holding the playing song into the list and returns its row. No playback command is sent.</summary>
    public async Task<TrackQueryRow?> LocatePlayingTrackAsync(CancellationToken cancellationToken)
    {
        var index = PlayingTrackIndex;
        return index < 0 ? null : await NavigateToTrackIndexAsync(index, cancellationToken);
    }

    /// <summary>Shows the page containing a global row index. No playback command is sent.</summary>
    public async Task<TrackQueryRow?> NavigateToTrackIndexAsync(int index, CancellationToken cancellationToken)
    {
        if (closing || client is null || IsGroupListVisible || index < 0 || index >= TotalCount || trackDragActive || savingTrackOrder || cancellationToken.IsCancellationRequested)
            return null;
        var identity = TrackNavigationIdentity;
        if (index >= TrackWindowOffset && index < TrackWindowOffset + Tracks.Count)
            return Tracks[index - TrackWindowOffset];
        // A direct offset query keeps jumps to distant markers independent of earlier pages.
        var pageStart = OFPlayer.WinUI.Core.Catalog.TrackWindowPaging.PageStart(index, PageSize);
        try
        {
            await LoadTrackWindowAsync(pageStart, append: false, prepend: false, cancellationToken, refreshIdentity: false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (closing || cancellationToken.IsCancellationRequested || identity != TrackNavigationIdentity || index < TrackWindowOffset || index >= TrackWindowOffset + Tracks.Count)
            return null;
        return Tracks[index - TrackWindowOffset];
    }
}
