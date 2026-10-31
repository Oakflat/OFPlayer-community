using System.Collections.ObjectModel;
using System.Text.Json;
using OFPlayer.WinUI.Core;
using OFPlayer_WinUI.Models;

namespace OFPlayer_WinUI.ViewModels;
/// <summary>Persisted queue info extracted from command results (display only).</summary>
public sealed record SessionQueueInfo(IReadOnlyList<string> QueueTrackIds, ulong? Revision, string? CurrentTrackId)
{
    public static SessionQueueInfo FromJson(JsonElement element)
    {
        var ids = new List<string>();
        if (element.TryGetProperty("queueTrackIds", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    ids.Add(item.GetString()!);
                }
            }
        }

        return new SessionQueueInfo(ids, OFPlayer.WinUI.Core.Playback.PlaybackStateOrder.ReadRevision(element, "queueRevision"), element.TryGetProperty("currentTrackId", out var track) && track.ValueKind == JsonValueKind.String ? track.GetString() : null);
    }
}

/// <summary>
/// Catalog navigation, paging, search and selection (W2, partial of MainViewModel).
///
/// Search and sorting run in the Rust host (single source of truth). The view model
/// only keeps the current page, cancels superseded requests and drops stale
/// responses via a request epoch so late answers can never overwrite newer input.
/// </summary>
public sealed partial class MainViewModel
{
    private const int PageSize = 200;
    private readonly List<LibraryNode> libraries = new();
    private readonly List<PlaylistNode> playlists = new();
    private string pageTitle = Services.Localization.Get("nav.library");
    private string activeLibraryId = string.Empty;
    private string? activeCollectionRef;
    private string? activeGroupKind;
    private string? parentArtist;
    private long artistAlbumsEpoch;
    public bool CanGoBackGroup => activeGroupKey is not null || parentArtist is not null;
    public bool IsArtistDetail => activeGroupKind == "artist" && activeGroupKey is not null;
    public bool IsArtistList => activeGroupKind == "artist" && activeGroupKey is null;
    public string ArtistSortOption { get; private set; } = "name";
    public bool NewTracksFirst { get; private set; }

    public async Task SetNewTracksFirstAsync(bool value)
    {
        NewTracksFirst = value;
        RaisePropertyChanged(nameof(NewTracksFirst));
        await PersistPreferencesAsync(new { newTracksFirst = value });
    }

    public async Task SetArtistSortOptionAsync(string value)
    {
        value = value == "track-count" ? value : "name";
        if (ArtistSortOption == value)
            return;
        ArtistSortOption = value;
        RaisePropertyChanged(nameof(ArtistSortOption));
        await PersistPreferencesAsync(new { artistSortOption = value });
        if (IsArtistList)
            await LoadGroupSummaryAsync();
    }

    public string GroupBackLabel => parentArtist is not null && activeGroupKind == "album" && activeGroupKey is not null ? Services.Localization.Format("library.back.to", ("name", parentArtist)) : Services.Localization.Get(activeGroupKind == "artist" || parentArtist is not null ? "library.back.artists" : "library.back.albums");

    private string? activeGroupKey;
    private string searchText = string.Empty;
    private string typeFilter = string.Empty;
    private string sortOption = "recent";
    private readonly OFPlayer.WinUI.Core.Playback.CollectionSortPreferences collectionSorting = new();
    private NavigationCounts navigationCounts = NavigationCounts.Empty;
    private IReadOnlyList<string> availableFormats = Array.Empty<string>();
    private int totalCount;
    private int selectedCount;
    private int queueTrackCount;
    private bool isLoadingPage;
    private bool isMultiSelect;
    private bool hasMore;
    private bool hasPrevious;
    private int trackWindowOffset;
    private long trackNavigationRevision;
    private CancellationTokenSource? pageRequest;
    private CancellationTokenSource? searchDebounce;
    private long pageEpoch;
    /// <summary>Full filtered id list for the current page query (queue source).</summary>
    private IReadOnlyList<string> currentCollectionTrackIds = Array.Empty<string>();
    private IReadOnlyList<string> PresentedQueueIds => currentQueueTrackIds;
    public IReadOnlyList<LibraryNode> Libraries => libraries;
    public ObservableCollection<GroupEntry> Groups { get; } = new();
    /// <summary>True while the album/artist group list is shown (not inside a group).</summary>
    public bool IsGroupListVisible => activeGroupKind is not null && activeGroupKey is null;
    /// <summary>True while browsing tracks inside one album/artist group.</summary>
    public bool IsInsideGroup => activeGroupKind is not null && activeGroupKey is not null;
    public NavigationCounts Counts => navigationCounts;
    public string ActiveLibraryId { get => activeLibraryId; private set => SetProperty(ref activeLibraryId, value); }
    public string? ActiveCollectionRef { get => activeCollectionRef; private set => SetProperty(ref activeCollectionRef, value); }
    public string SearchText { get => searchText; private set => SetProperty(ref searchText, value); }
    public string SortOption { get => sortOption; private set => SetProperty(ref sortOption, value); }
    public string TypeFilter { get => typeFilter; private set => SetProperty(ref typeFilter, value); }
    public IReadOnlyList<string> AvailableFormats { get => availableFormats; private set => SetProperty(ref availableFormats, value); }
    public int TotalCount { get => totalCount; private set => SetProperty(ref totalCount, value); }
    public int LoadedCount => Tracks.Count;
    public int SelectedCount { get => selectedCount; private set => SetProperty(ref selectedCount, value); }
    public int QueueTrackCount { get => queueTrackCount; private set => SetProperty(ref queueTrackCount, value); }
    public bool IsLoadingPage { get => isLoadingPage; private set => SetProperty(ref isLoadingPage, value); }
    public bool IsMultiSelect { get => isMultiSelect; private set => SetProperty(ref isMultiSelect, value); }
    public bool HasMore { get => hasMore; private set => SetProperty(ref hasMore, value); }
    /// <summary>Zero-based global index of the first displayed track.</summary>
    public int TrackWindowOffset { get => trackWindowOffset; private set => SetProperty(ref trackWindowOffset, value); }
    public bool HasPrevious { get => hasPrevious; private set => SetProperty(ref hasPrevious, value); }
    /// <summary>Changes for each query and authoritative refresh; preview caches use this key.</summary>
    public string TrackNavigationIdentity => string.Join("|", new[] { ActiveLibraryId, ActiveCollectionRef ?? "", activeGroupKind ?? "", activeGroupKey ?? "", parentArtist ?? "", SearchText, TypeFilter, SortOption, trackNavigationRevision.ToString(), }.Select(value => $"{value.Length}:{value}"));

    public IReadOnlyList<PlaylistNode> PlaylistsForLibrary(string libraryId, bool includeSystem = false) => playlists.Where(playlist => playlist.LibraryId == libraryId && (includeSystem || playlist.Kind != "system")).ToList();
    public void ToggleMultiSelect() => IsMultiSelect = !IsMultiSelect;
    public void SetSelectedRows(IReadOnlyList<TrackQueryRow> rows) => SelectedCount = rows.Count;
    public void SetSortOption(string option)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Catalog.SetSortOption");
        option = OFPlayer.WinUI.Core.Playback.CollectionSortPreferences.Normalize(option);
        if (SortOption == option)
        {
            return;
        }

        SortOption = option;
        InvalidateTrackNavigation();
        _ = PersistPreferencesAsync(collectionSorting.Set(ActiveCollectionRef, option));
        _ = LoadFirstPageAsync();
    }

    public void SetTypeFilter(string format)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Catalog.SetTypeFilter");
        var normalized = format ?? string.Empty;
        if (TypeFilter == normalized)
        {
            return;
        }

        TypeFilter = normalized;
        InvalidateTrackNavigation();
        _ = LoadFirstPageAsync();
    }

    /// <summary>Debounced search: old keystrokes cancel their pending requests.</summary>
    public void SetSearchText(string text)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Catalog.SetSearchText");
        SearchText = text ?? string.Empty;
        InvalidateTrackNavigation();
        searchDebounce?.Cancel();
        searchDebounce?.Dispose();
        searchDebounce = new CancellationTokenSource();
        var token = searchDebounce.Token;
        _ = SearchAfterDelayAsync(token);
    }

    private async Task SearchAfterDelayAsync(CancellationToken token)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Catalog.SearchAfterDelayAsync");
        try
        {
            await Task.Delay(250, token);
            if (!closing && !IsGroupListVisible)
                await LoadFirstPageAsync();
        }
        catch (OperationCanceledException)
        {
        // A newer keystroke superseded this search.
        }
    }

    public Task SelectLibraryAsync(string libraryId)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Catalog.SelectLibraryAsync");
        if (libraries.FirstOrDefault(l => l.Id == libraryId)?.IsLocked == true)
        {
            StatusMessage = Services.Localization.Get("quota.locked");
            return Task.CompletedTask;
        }

        var available = PlaylistsForLibrary(libraryId, includeSystem: true);
        var initial = available.FirstOrDefault(playlist => playlist.SystemKey == "all-tracks") ?? available.FirstOrDefault();
        return initial is not null ? SelectPlaylistAsync(libraryId, initial.Id) : SelectCollectionInternalAsync(libraryId, "view:recent-imports", Services.Localization.Get("nav.recentImports"));
    }

    public Task SelectPlaylistAsync(string libraryId, string playlistId)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Catalog.SelectPlaylistAsync");
        var playlist = playlists.FirstOrDefault(node => node.Id == playlistId && node.LibraryId == libraryId);
        if (playlist?.IsLocked == true)
        {
            StatusMessage = Services.Localization.Get("quota.locked");
            return Task.CompletedTask;
        }

        var title = playlist?.SystemKey == "all-tracks" ? Services.Localization.Get("nav.allSongs") : playlist?.Name ?? Services.Localization.Get("nav.playlist");
        return SelectCollectionInternalAsync(libraryId, $"playlist:{playlistId}", title);
    }

    public Task SelectViewAsync(string viewKey)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Catalog.SelectViewAsync");
        var title = ViewTitle(viewKey);
        if (viewKey is "albums" or "artists")
        {
            return SelectGroupViewInternalAsync(viewKey == "albums" ? "album" : "artist", title);
        }

        return SelectCollectionInternalAsync(ActiveLibraryId, $"view:{viewKey}", title);
    }

    /// <summary>Opens one album/artist group and pages its tracks.</summary>
    public async Task OpenGroupAsync(GroupEntry entry)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Catalog.OpenGroupAsync");
        if (client is null)
        {
            return;
        }

        var library = ActiveLibraryId;
        post(() =>
        {
            Interlocked.Increment(ref artistAlbumsEpoch);
            if (entry.Kind == "artist")
            {
                parentArtist = entry.Key;
                activeGroupKind = "artist";
                activeGroupKey = entry.Key;
                Groups.Clear();
            }
            else
            {
                activeGroupKind = entry.Kind;
                activeGroupKey = entry.Key;
            }

            PageTitle = entry.Label;
            RaisePropertyChanged(nameof(IsGroupListVisible));
            RaisePropertyChanged(nameof(IsInsideGroup));
            InvalidateTrackNavigation();
        });
        await LoadFirstPageAsync();
        if (entry.Kind == "artist" && IsArtistDetail && activeGroupKey == entry.Key && ActiveLibraryId == library)
            await LoadGroupSummaryAsync();
    }

    /// <summary>Returns from a group back to its group list.</summary>
    public async Task BackToGroupsAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Catalog.BackToGroupsAsync");
        if (client is null || activeGroupKind is null)
        {
            return;
        }

        post(() =>
        {
            Interlocked.Increment(ref artistAlbumsEpoch);
            if (activeGroupKind == "album" && parentArtist is not null)
            {
                activeGroupKind = "artist";
                activeGroupKey = parentArtist;
            }
            else
            {
                parentArtist = null;
                activeGroupKey = null;
            }

            PageTitle = parentArtist ?? ViewTitle(activeGroupKind == "artist" ? "artists" : "albums");
            RaisePropertyChanged(nameof(IsGroupListVisible));
            RaisePropertyChanged(nameof(IsInsideGroup));
            InvalidateTrackNavigation();
        });
        if (IsArtistDetail)
        {
            var artist = activeGroupKey;
            var library = ActiveLibraryId;
            await LoadFirstPageAsync();
            if (!IsArtistDetail || activeGroupKey != artist || ActiveLibraryId != library)
                return;
        }

        await LoadGroupSummaryAsync();
    }

    public async Task LoadMoreAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Catalog.LoadMoreAsync");
        if (IsLoadingPage || !HasMore || trackDragActive || savingTrackOrder)
        {
            return;
        }

        await LoadPageAsync(OFPlayer.WinUI.Core.Catalog.TrackWindowPaging.NextOffset(TrackWindowOffset, Tracks.Count), append: true);
    }

    public async Task LoadPreviousAsync()
    {
        if (IsLoadingPage || !HasPrevious || trackDragActive || savingTrackOrder)
            return;
        var start = OFPlayer.WinUI.Core.Catalog.TrackWindowPaging.PreviousStart(TrackWindowOffset, PageSize);
        await LoadTrackWindowAsync(start, append: false, prepend: true, CancellationToken.None);
    }

    /// <summary>
    /// Loads the library/playlist structure. `selectDefault` selects the first
    /// library after loading (startup); management mutations pass false so the
    /// current selection survives renames/reorders.
    /// </summary>
    private async Task LoadStructureAsync(bool selectDefault = true)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Catalog.LoadStructureAsync");
        if (client is null)
        {
            return;
        }

        var structureJson = await client.SendAsync("desktop_catalog_load_structure", null, CancellationToken.None);
        var structure = CatalogStructure.FromJson(structureJson);
        libraries.Clear();
        libraries.AddRange(structure.Libraries);
        playlists.Clear();
        playlists.AddRange(structure.Playlists);
        post(() =>
        {
            NavigationChanged?.Invoke();
            if (selectDefault && libraries.Any(l => !l.IsLocked))
            {
                _ = SelectLibraryAsync(libraries.First(l => !l.IsLocked).Id);
            }
        });
    }

    private async Task SelectCollectionInternalAsync(string libraryId, string? collectionRef, string title)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Catalog.SelectCollectionInternalAsync");
        post(() =>
        {
            ActiveLibraryId = libraryId;
            ActiveCollectionRef = collectionRef;
            SortOption = collectionSorting.Resolve(collectionRef);
            PageTitle = title;
            parentArtist = null;
            activeGroupKind = null;
            activeGroupKey = null;
            // Keep the hidden group containers alive for a return to this browser.
            RaisePropertyChanged(nameof(IsGroupListVisible));
            RaisePropertyChanged(nameof(IsInsideGroup));
            InvalidateTrackNavigation();
            // Navigation is usable as soon as its local destination is known.
            // Optional counts must not gate the All Songs entry or its selection.
            NavigationChanged?.Invoke();
        });
        await Task.WhenAll(RefreshNavigationAsync(), LoadFirstPageAsync());
    }

    private async Task SelectGroupViewInternalAsync(string groupKind, string title)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Catalog.SelectGroupViewInternalAsync");
        post(() =>
        {
            ActiveCollectionRef = $"view:{groupKind}s";
            SortOption = collectionSorting.Resolve(ActiveCollectionRef);
            PageTitle = title;
            parentArtist = null;
            activeGroupKind = groupKind;
            activeGroupKey = null;
            RaisePropertyChanged(nameof(IsGroupListVisible));
            RaisePropertyChanged(nameof(IsInsideGroup));
            InvalidateTrackNavigation();
            NavigationChanged?.Invoke();
        });
        await Task.WhenAll(RefreshNavigationAsync(), LoadGroupSummaryAsync());
        if (groupKind == "artist" && activeGroupKind == "artist" && CanImport)
            await RunImportAsync("desktop_catalog_refresh_browse_metadata", new { libraryId = ActiveLibraryId }, Services.Localization.Get("import.artists.refresh"));
    }

    private async Task LoadGroupSummaryAsync()
    {
        if (IsArtistDetail)
        {
            await LoadArtistAlbumsAsync();
            return;
        }

        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Catalog.LoadGroupSummaryAsync");
        var activeClient = client;
        if (activeClient is null || activeGroupKind is null)
        {
            return;
        }

        rowArtworkRequests.Clear();
        pageRequest?.Cancel();
        pageRequest?.Dispose();
        pageRequest = new CancellationTokenSource();
        var epoch = Interlocked.Increment(ref pageEpoch);
        var token = pageRequest.Token;
        post(() => IsLoadingPage = true);
        try
        {
            var artistDetail = IsArtistDetail;
            var groupKind = artistDetail ? "album" : activeGroupKind;
            var result = await activeClient.SendAsync("desktop_catalog_group_summary", new { activeLibrary = ActiveLibraryId, groupKind, artistFilter = parentArtist }, token);
            var groups = GroupEntry.ListFromJson(result, groupKind);
            if (groupKind == "artist" && ArtistSortOption == "track-count")
                groups = groups.OrderByDescending(group => group.TrackCount).ThenBy(group => group.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (Interlocked.Read(ref pageEpoch) != epoch)
            {
                return;
            }

            post(() =>
            {
                if (closing || activeClient != client || Interlocked.Read(ref pageEpoch) != epoch)
                    return;
                // Reconcile instead of Reset: unchanged cards retain their images and containers.
                for (var index = 0; index < groups.Count; index++)
                {
                    var group = groups[index];
                    var existing = -1;
                    for (var candidate = index; candidate < Groups.Count; candidate++)
                        if (Groups[candidate].Kind == group.Kind && Groups[candidate].Key == group.Key)
                        {
                            existing = candidate;
                            break;
                        }

                    if (existing < 0)
                        Groups.Insert(index, group);
                    else
                    {
                        if (existing != index)
                            Groups.Move(existing, index);
                        var previous = Groups[index];
                        // Aggregation order can change sub-second floating-point totals.
                        // Only replace a card when its displayed data or artwork changed.
                        if (previous.Label != group.Label || previous.SummaryText != group.SummaryText || previous.SampleTrackId != group.SampleTrackId)
                            Groups[index] = group;
                    }
                }

                while (Groups.Count > groups.Count)
                    Groups.RemoveAt(Groups.Count - 1);
                if (!artistDetail)
                {
                    Tracks.Clear();
                    TotalCount = groups.Count;
                    HasMore = false;
                    HasPrevious = false;
                    TrackWindowOffset = 0;
                    InvalidateTrackNavigation();
                    RaisePropertyChanged(nameof(LoadedCount));
                }
            });
        }
        catch (OperationCanceledException)when (token.IsCancellationRequested || Interlocked.Read(ref pageEpoch) != epoch || closing)
        {
        // Superseded by a newer query or shutdown.
        }
        catch (Exception error)
        {
            if (Interlocked.Read(ref pageEpoch) == epoch)
            {
                ReportCommandError(error, isCurrent: () => Interlocked.Read(ref pageEpoch) == epoch && activeClient == client);
            }
        }
        finally
        {
            if (Interlocked.Read(ref pageEpoch) == epoch)
            {
                post(() => IsLoadingPage = false);
            }
        }
    }

    private async Task LoadArtistAlbumsAsync()
    {
        var activeClient = client;
        var artist = activeGroupKey;
        var library = ActiveLibraryId;
        if (activeClient is null || artist is null || !IsArtistDetail)
            return;
        var epoch = Interlocked.Increment(ref artistAlbumsEpoch);
        bool IsCurrent() => !closing && client == activeClient && IsArtistDetail && activeGroupKey == artist && ActiveLibraryId == library && Interlocked.Read(ref artistAlbumsEpoch) == epoch;
        try
        {
            // Album cards have their own request lifetime. Sorting/paging songs must not cancel them,
            // and an album response must never cancel or clear the song query.
            var result = await activeClient.SendAsync("desktop_catalog_group_summary", new { activeLibrary = library, groupKind = "album", artistFilter = artist }, CancellationToken.None);
            var albums = GroupEntry.ListFromJson(result, "album");
            post(() =>
            {
                if (!IsCurrent())
                    return;
                Groups.Clear();
                foreach (var album in albums)
                    Groups.Add(album);
            });
        }
        catch (Exception error)
        {
            ReportCommandError(error, isCurrent: IsCurrent);
        }
    }

    private async Task RefreshNavigationAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Catalog.RefreshNavigationAsync");
        if (client is null)
        {
            return;
        }

        var activeClient = client;
        var requestedLibrary = ActiveLibraryId;
        var requestedCollection = ActiveCollectionRef;
        try
        {
            var navigation = await activeClient.SendAsync("desktop_catalog_resolve_navigation", new { activeLibrary = requestedLibrary, activeCollectionRef = requestedCollection }, CancellationToken.None);
            var counts = NavigationCounts.FromJson(navigation);
            post(() =>
            {
                if (closing || activeClient != client || ActiveLibraryId != requestedLibrary || ActiveCollectionRef != requestedCollection)
                    return;
                navigationCounts = counts;
                RaisePropertyChanged(nameof(Counts));
                NavigationChanged?.Invoke();
            });
        }
        catch (Exception error)
        {
            ReportCommandError(error, isCurrent: () => activeClient == client && ActiveLibraryId == requestedLibrary && ActiveCollectionRef == requestedCollection);
        }
    }

    private Task LoadFirstPageAsync() => LoadPageAsync(0, append: false);
    private Task LoadPageAsync(int offset, bool append) => LoadTrackWindowAsync(offset, append, prepend: false, CancellationToken.None);
    private async Task LoadTrackWindowAsync(int offset, bool append, bool prepend, CancellationToken cancellationToken, bool refreshIdentity = true)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Catalog.LoadPageAsync");
        if (!append && !prepend)
            rowArtworkRequests.Clear();
        var activeClient = client;
        if (activeClient is null)
        {
            return;
        }

        pageRequest?.Cancel();
        pageRequest?.Dispose();
        pageRequest = new CancellationTokenSource();
        var epoch = Interlocked.Increment(ref pageEpoch);
        using var linkedRequest = CancellationTokenSource.CreateLinkedTokenSource(pageRequest.Token, cancellationToken);
        var token = linkedRequest.Token;
        var requestedLibrary = ActiveLibraryId;
        var requestedCollection = ActiveCollectionRef;
        var requestedGroupKind = activeGroupKind;
        var requestedGroupKey = activeGroupKey;
        var requestedArtist = parentArtist;
        var requestedSearch = SearchText;
        var requestedType = TypeFilter;
        var requestedSort = SortOption;
        if (!append && !prepend && refreshIdentity)
        {
            InvalidateTrackNavigation();
        }

        post(() =>
        {
            if (Interlocked.Read(ref pageEpoch) == epoch)
                IsLoadingPage = true;
        });
        try
        {
            TrackQueryPage? projectedPage = null;
            var result = requestedGroupKey is null ? await activeClient.SendAsync("desktop_catalog_query_collection_tracks", new { activeLibrary = requestedLibrary, activeCollectionRef = requestedCollection, searchQuery = requestedSearch, typeFilter = requestedType, sortOption = requestedSort, offset, limit = PageSize, includeTrackIds = !append && !prepend, }, token) : await activeClient.SendAsync("desktop_catalog_query_group_tracks", new { groupKind = requestedGroupKind, groupKey = requestedGroupKey, artistFilter = requestedArtist, activeLibrary = requestedLibrary, searchQuery = requestedSearch, typeFilter = requestedType, sortOption = requestedSort, offset, limit = PageSize, includeTrackIds = !append && !prepend, }, token);
            var page = projectedPage ?? TrackQueryPage.FromJson(result);
            if (page.Rows.Count > 0 && page.Offset != offset)
                throw new HostProtocolException($"Track page offset mismatch: requested {offset}, received {page.Offset}.");
            if (Interlocked.Read(ref pageEpoch) != epoch || token.IsCancellationRequested)
            {
                // A newer request superseded this response; never overwrite newer state.
                return;
            }

            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            post(() =>
            {
                try
                {
                    if (closing || activeClient != client || Interlocked.Read(ref pageEpoch) != epoch || token.IsCancellationRequested || requestedLibrary != ActiveLibraryId || requestedCollection != ActiveCollectionRef || requestedGroupKind != activeGroupKind || requestedGroupKey != activeGroupKey || requestedArtist != parentArtist || requestedSearch != SearchText || requestedType != TypeFilter || requestedSort != SortOption)
                        return;
                    if (!append && !prepend)
                    {
                        Tracks.Clear();
                        currentCollectionTrackIds = page.TrackIds;
                        TrackWindowOffset = offset;
                    }
                    else if (prepend)
                    {
                        TrackWindowOffset = offset;
                    }

                    var addedRows = new List<TrackQueryRow>();
                    for (var position = 0; position < page.Rows.Count; position++)
                    {
                        var row = page.Rows[position] with
                        {
                            IsActive = page.Rows[position].Id == playback.ActiveTrackId,
                            RowNumber = offset + position + 1,
                        };
                        {
                            if (prepend)
                                Tracks.Insert(position, row);
                            else
                                Tracks.Add(row);
                        }

                        addedRows.Add(row);
                    };
                    _ = CheckLocalFilesAsync(addedRows, token);
                    TotalCount = page.TotalCount;
                    HasPrevious = TrackWindowOffset > 0;
                    HasMore = OFPlayer.WinUI.Core.Catalog.TrackWindowPaging.HasMore(TrackWindowOffset, Tracks.Count, page.TotalCount);
                    AvailableFormats = page.AvailableFormats;
                    RaisePropertyChanged(nameof(LoadedCount));
                }
                catch (Exception error)
                {
                    applied.TrySetException(error);
                }
                finally
                {
                    applied.TrySetResult();
                }
            });
            await applied.Task.WaitAsync(token);
        }
        catch (OperationCanceledException)when (token.IsCancellationRequested || Interlocked.Read(ref pageEpoch) != epoch || closing)
        {
        // Superseded by a newer query or shutdown.
        }
        catch (Exception error)
        {
            if (Interlocked.Read(ref pageEpoch) == epoch)
            {
                ReportCommandError(error, isCurrent: () => Interlocked.Read(ref pageEpoch) == epoch && activeClient == client);
            }
        }
        finally
        {
            if (Interlocked.Read(ref pageEpoch) == epoch)
            {
                post(() =>
                {
                    if (Interlocked.Read(ref pageEpoch) == epoch)
                        IsLoadingPage = false;
                });
            }
        }
    }
}
