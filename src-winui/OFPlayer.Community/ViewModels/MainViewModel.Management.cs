using System.Text.Json;
using OFPlayer_WinUI.Models;

namespace OFPlayer_WinUI.ViewModels;
public enum ImportOutcome
{
    Succeeded,
    Cancelled,
    Failed,
    Skipped
}

/// <summary>
/// N1 local management: library/playlist CRUD, track favorite/batch delete,
/// playlist membership and the async import pipeline (files + directory scan).
///
/// All mutations run through the Rust host (single-writer); the view model only
/// forwards validated ids, mirrors progress events for the UI and refreshes the
/// structure/page after a mutation. Imports execute on a host worker thread, so
/// playback controls stay responsive while scanning runs.
/// </summary>
public sealed partial class MainViewModel
{
    public JsonElement LastPlaylistImportResult { get; private set; }

    public async Task<ImportOutcome> StartImportPlaylistAsync(string path)
    {
        LastPlaylistImportResult = default;
        if (!CanImport)
            return ImportOutcome.Skipped;
        return await RunImportAsync("desktop_playlist_import_m3u", new { libraryId = ActiveLibraryId, path }, System.IO.Path.GetFileName(path));
    }

    private bool isImporting;
    // Shared by imports and confirmed missing-track cleanup. Take it before the
    // first await so a watcher and a manual click cannot start two host scans.
    private int importOperationGate;
    private double importPercent;
    private string importStatusText = string.Empty;
    public bool IsImporting { get => isImporting; private set => SetProperty(ref isImporting, value); }
    public double ImportPercent { get => importPercent; private set => SetProperty(ref importPercent, value); }
    public string ImportStatusText { get => importStatusText; private set => SetProperty(ref importStatusText, value); }
    public bool CanImport => IsConnected && !IsImporting && ActiveLibraryId.Length > 0;

    // ---- 曲库 ----
    public async Task<string?> CreateLibraryAsync(string name)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Management.CreateLibraryAsync");
        if (client is null)
        {
            return null;
        }

        try
        {
            var result = await client.SendAsync("desktop_library_create", new { name }, CancellationToken.None);
            var libraryId = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("library", out var library) && library.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            await RefreshAfterMutationAsync(Services.Localization.Format("done.library.created", ("name", name)));
            if (!string.IsNullOrEmpty(libraryId))
            {
                await SelectLibraryAsync(libraryId);
            }

            return libraryId;
        }
        catch (Exception error)
        {
            ReportCommandError(error);
            return null;
        }
    }

    public Task RenameLibraryAsync(string libraryId, string name) => MutateAsync("desktop_library_rename", new { libraryId, name }, Services.Localization.Format("done.library.renamed", ("name", name)));
    public Task DeleteLibraryAsync(string libraryId, string label) => MutateAsync("desktop_library_delete", new { libraryId }, Services.Localization.Format("done.library.deleted", ("name", label)));
    public Task MoveLibraryAsync(string libraryId, int delta)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Management.MoveLibraryAsync");
        var ids = Libraries.Select(library => library.Id).ToList();
        var index = ids.IndexOf(libraryId);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= ids.Count)
        {
            return Task.CompletedTask;
        }

        (ids[index], ids[target]) = (ids[target], ids[index]);
        return MutateAsync("desktop_library_reorder", new { orderedLibraryIds = ids }, Services.Localization.Get("done.library.reordered"));
    }

    // ---- 歌单 ----
    public async Task<string?> CreatePlaylistAsync(string libraryId, string name)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Management.CreatePlaylistAsync");
        if (client is null || string.IsNullOrEmpty(libraryId))
        {
            return null;
        }

        try
        {
            var result = await client.SendAsync("desktop_playlist_create", new { libraryId, name }, CancellationToken.None);
            var playlistId = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            await RefreshAfterMutationAsync(Services.Localization.Format("done.playlist.created", ("name", name)));
            return playlistId;
        }
        catch (Exception error)
        {
            ReportCommandError(error);
            return null;
        }
    }

    public Task RenamePlaylistAsync(string playlistId, string name) => MutateAsync("desktop_playlist_rename", new { playlistId, name }, Services.Localization.Format("done.playlist.renamed", ("name", name)));
    public Task DeletePlaylistAsync(string playlistId, string label) => MutateAsync("desktop_playlist_delete", new { playlistId }, Services.Localization.Format("done.playlist.deleted", ("name", label)));
    public Task MovePlaylistAsync(string libraryId, string playlistId, int delta)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Management.MovePlaylistAsync");
        var ids = PlaylistsForLibrary(libraryId).Select(playlist => playlist.Id).ToList();
        var index = ids.IndexOf(playlistId);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= ids.Count)
        {
            return Task.CompletedTask;
        }

        (ids[index], ids[target]) = (ids[target], ids[index]);
        return MutateAsync("desktop_playlist_reorder", new { libraryId, orderedPlaylistIds = ids }, Services.Localization.Get("done.playlist.reordered"));
    }

    public async Task AddTracksToPlaylistAsync(string playlistId, IReadOnlyList<string> trackIds)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Management.AddTracksToPlaylistAsync");
        if (client is null || string.IsNullOrEmpty(playlistId) || trackIds.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var trackId in trackIds)
            {
                await client.SendAsync("desktop_playlist_add_track", new { playlistId, trackId }, CancellationToken.None);
            }

            await RefreshAfterMutationAsync(Services.Localization.Format("done.playlist.added", ("count", trackIds.Count.ToString())));
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    /// <summary>Alignment: drag reorder inside a playlist (old front-end drag-and-drop).</summary>
    public Task ReorderPlaylistTracksAsync(string playlistId, IReadOnlyList<string> orderedTrackIds) => MutateAsync("desktop_playlist_reorder_tracks", new { playlistId, orderedTrackIds }, Services.Localization.Get("done.order.saved"));
    public async Task RemoveTracksFromPlaylistAsync(string playlistId, IReadOnlyList<string> trackIds)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Management.RemoveTracksFromPlaylistAsync");
        if (client is null || string.IsNullOrEmpty(playlistId) || trackIds.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var trackId in trackIds)
            {
                await client.SendAsync("desktop_playlist_remove_track", new { playlistId, trackId }, CancellationToken.None);
            }

            await RefreshAfterMutationAsync(Services.Localization.Format("done.playlist.removed", ("count", trackIds.Count.ToString())));
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    // ---- 曲目（收藏 / 批量删除）----
    public async Task SetFavoriteAsync(string trackId, bool isFavorite)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Management.SetFavoriteAsync");
        if (client is null || string.IsNullOrEmpty(trackId))
        {
            return;
        }

        // Optimistic: the star flips on click and the row is updated in place, so the
        // list keeps its scroll position and no row is re-created.
        var row = Tracks.FirstOrDefault(candidate => candidate.Id == trackId);
        var previous = row?.Favorite;
        if (row is not null)
            post(() => row.Favorite = isFavorite);
        try
        {
            var result = await client.SendAsync("desktop_track_set_favorite", new { trackId, isFavorite }, CancellationToken.None);
            var confirmed = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("isFavorite", out var favoriteElement) && favoriteElement.ValueKind == JsonValueKind.True;
            post(() =>
            {
                if (row is not null)
                    row.Favorite = confirmed;
                // In the favorites view an unstarred row leaves the list; the rows
                // below close the gap instead of the whole page reloading.
                if (!confirmed && row is not null && ActiveCollectionRef == "view:all-favorites" && Tracks.Remove(row))
                {
                    TotalCount = Math.Max(0, TotalCount - 1);
                    RenumberTracks();
                    RaisePropertyChanged(nameof(LoadedCount));
                }
            });
            await RefreshNavigationAsync();
        }
        catch (Exception error)
        {
            if (row is not null && previous is bool restore)
                post(() => row.Favorite = restore);
            ReportCommandError(error);
        }
    }

    public async Task DeleteTracksAsync(IReadOnlyList<string> trackIds, string label)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Management.DeleteTracksAsync");
        if (client is null || trackIds.Count == 0)
        {
            return;
        }

        try
        {
            var result = await client.SendAsync("desktop_tracks_delete_from_library", new { trackIds }, CancellationToken.None);
            ApplyCommandResult(result);
            SelectedCount = 0;
            await RefreshAfterMutationAsync(label);
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    // ---- 导入（文件 / 目录扫描）----
    public async Task StartImportFilesAsync(IReadOnlyList<string> sourcePaths)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Management.StartImportFilesAsync");
        if (sourcePaths.Count == 0)
        {
            return;
        }

        var files = sourcePaths.Select(path => new { sourcePath = path, fileName = System.IO.Path.GetFileName(path) }).ToArray();
        await RunImportAsync("desktop_library_import_files", new { libraryId = ActiveLibraryId, files }, Services.Localization.Format("import.label.files", ("count", files.Length.ToString())));
    }

    public async Task StartImportDirectoryAsync(string directory)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Management.StartImportDirectoryAsync");
        var label = System.IO.Path.GetFileName(directory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        label = string.IsNullOrEmpty(label) ? directory : label;
        await RunImportAsync("desktop_library_scan_import", new { libraryId = ActiveLibraryId, directories = new[] { directory }, // Interactive folder import ignores previously-deleted paths, matching
 // the old front-end (runLibraryScanImport with interactive: true).
        respectDeletedImportPaths = false, }, label);
    }

    private async Task<ImportOutcome> RunImportAsync(string command, object args, string label, string? targetLibraryId = null)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Management.RunImportAsync");
        var libraryId = targetLibraryId ?? ActiveLibraryId;
        if (client is null || IsRemoteMode || string.IsNullOrEmpty(libraryId) || ActiveLibraryId != libraryId || Interlocked.CompareExchange(ref importOperationGate, 1, 0) != 0)
        {
            return ImportOutcome.Skipped;
        }

        var activeClient = client;
        var isScan = command == "desktop_library_scan_import";
        if (isScan)
        {
            // Any later scan replaces the host's one-shot snapshot. Its old UI
            // prompt must stop offering an expired token immediately.
            post(() => PendingMissingTracksReview = null);
        }

        post(() =>
        {
            IsImporting = true;
            ImportPercent = 0;
            ImportStatusText = Services.Localization.Format("import.running", ("label", label));
        });
        try
        {
            // Import/scan is a progress-reporting job with a native cancel
            // command, not a 15-second interactive read. Keep its receipt until
            // completion/cancellation (or host exit), including first indexing.
            var result = await activeClient.SendAsync(command, args, Timeout.InfiniteTimeSpan, CancellationToken.None);
            var imported = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("importedTracks", out var importedTracks) && importedTracks.ValueKind == JsonValueKind.Array ? importedTracks.GetArrayLength() : 0;
            var cancelled = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("cancelled", out var cancelledElement) && cancelledElement.ValueKind == JsonValueKind.True;
            var skippedCount = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("skippedCount", out var skippedElement) && skippedElement.ValueKind == JsonValueKind.Number ? skippedElement.GetInt32() : 0;
            var firstSkipped = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("skippedFiles", out var skippedFilesElement) && skippedFilesElement.ValueKind == JsonValueKind.Array && skippedFilesElement.GetArrayLength() > 0 ? skippedFilesElement[0] : default;
            var skippedDetail = firstSkipped.ValueKind == JsonValueKind.Object && firstSkipped.TryGetProperty("fileName", out var skippedName) && skippedName.ValueKind == JsonValueKind.String // The host's reason is an engineering sentence (and not in the app's language): the file name is enough to find it.
 ? Services.Localization.Format("import.skipped.example", ("name", skippedName.GetString() ?? string.Empty)) : string.Empty;
            ApplyCommandResult(result);
            if (command == "desktop_catalog_refresh_browse_metadata" && !cancelled && ReadInt(result, "updatedMetadataCount") == 0 && skippedCount == 0)
                return ImportOutcome.Succeeded;
            if (command == "desktop_playlist_import_m3u")
                LastPlaylistImportResult = result.Clone();
            var message = cancelled ? Services.Localization.Format("import.cancelled", ("count", imported.ToString())) : imported == 0 ? Services.Localization.Format("import.nothingNew", ("label", label)) : Services.Localization.Format("import.done", ("count", imported.ToString()));
            if (result.TryGetProperty("playlist", out var playlist) && playlist.ValueKind == JsonValueKind.Object)
                message = Services.Localization.Format("import.playlist.done", ("name", ReadString(playlist, "name") ?? string.Empty), ("count", ReadInt(playlist, "trackCount").ToString()));
            if (command == "desktop_catalog_refresh_browse_metadata" && !cancelled)
                message = Services.Localization.Format("import.artists.done", ("count", ReadInt(result, "updatedMetadataCount").ToString()));
            if (!cancelled && skippedCount > 0)
            {
                message += Services.Localization.Format("import.skipped", ("count", skippedCount.ToString()), ("example", skippedDetail));
            }

            if (result.TryGetProperty("fileTracking", out var tracking))
            {
                if (tracking.TryGetProperty("incomplete", out var incomplete) && incomplete.ValueKind == JsonValueKind.True)
                    message += " " + Services.Localization.Get("import.trackingIncomplete");
                foreach (var(field, text)in new[]
                {
                    ("relinkedCount", "import.relinked"),
                    ("unresolvedMissingCount", "import.untrackedMissing"),
                    ("ambiguousCount", "import.ambiguousLocations"),
                    ("unreadableCount", "import.trackingUnreadable")
                }

                )
                {
                    var count = ReadInt(tracking, field);
                    if (field == "unresolvedMissingCount")
                        count = Math.Max(0, count - ReadInt(tracking, "ambiguousCount"));
                    if (count > 0)
                        message += " " + Services.Localization.Format(text, ("count", count.ToString()));
                }
            }

            await RefreshAfterMutationAsync(message);
            if (isScan && !cancelled && activeClient == client)
            {
                SetMissingTracksReviewFromScan(result, libraryId);
            }

            return cancelled ? ImportOutcome.Cancelled : ImportOutcome.Succeeded;
        }
        catch (Exception error)
        {
            ReportCommandError(error);
            return ImportOutcome.Failed;
        }
        finally
        {
            post(() =>
            {
                IsImporting = false;
                ImportStatusText = string.Empty;
                Interlocked.Exchange(ref importOperationGate, 0);
            });
        }
    }

    public async Task CancelImportAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Management.CancelImportAsync");
        if (client is null || !IsImporting)
        {
            return;
        }

        try
        {
            await client.SendAsync("desktop_import_cancel", null, CancellationToken.None);
            post(() => ImportStatusText = Services.Localization.Get("import.cancelling"));
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    /// <summary>Mirrors `import.progress` host events (worker thread) onto the UI.</summary>
    public void ApplyImportProgress(JsonElement data)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Management.ApplyImportProgress");
        var phase = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("phase", out var phaseElement) ? phaseElement.GetString() : null;
        var processed = ReadInt(data, "processed");
        var total = ReadInt(data, "total");
        var candidateTotal = ReadInt(data, "candidateTotal");
        var currentFile = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("currentFile", out var fileElement) ? fileElement.GetString() : null;
        post(() =>
        {
            ImportPercent = total > 0 ? Math.Clamp(processed * 100.0 / total, 0, 100) : 0;
            var phaseText = phase switch
            {
                "discovering" => Services.Localization.Get("import.phase.scanning"),
                "tracking" => Services.Localization.Get("import.phase.tracking"),
                "processing" => Services.Localization.Get(candidateTotal > 0 ? "import.phase.importing" : "import.phase.working"),
                _ => Services.Localization.Get("import.phase.working"),
            };
            var detail = total > 0 ? $"{processed}/{total}" : string.Empty;
            if (!string.IsNullOrEmpty(currentFile))
            {
                detail = detail.Length > 0 ? $"{detail} · {System.IO.Path.GetFileName(currentFile)}" : System.IO.Path.GetFileName(currentFile);
            }

            ImportStatusText = detail.Length > 0 ? $"{phaseText} {detail}" : phaseText;
        });
    }

    private static int ReadInt(JsonElement data, string field) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(field, out var element) && element.ValueKind == JsonValueKind.Number ? element.GetInt32() : 0;
    // ---- 通用 ----
    private async Task MutateAsync(string command, object args, string successMessage)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Management.MutateAsync");
        if (client is null)
        {
            return;
        }

        try
        {
            var result = await client.SendAsync(command, args, CancellationToken.None);
            // Delete-style mutations carry the post-mutation session/playback.
            ApplyCommandResult(result);
            await RefreshAfterMutationAsync(successMessage);
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    /// <summary>
    /// Reloads the structure without resetting the selection, falls back when the
    /// selected library/playlist disappeared, then refreshes counts and the page.
    /// </summary>
    private async Task RefreshAfterMutationAsync(string successMessage)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Management.RefreshAfterMutationAsync");
        post(() =>
        {
            StatusKind = StatusKind.Success;
            StatusMessage = successMessage;
        });
        await LoadStructureAsync(selectDefault: false);
        if (!libraries.Any(library => library.Id == ActiveLibraryId))
        {
            if (libraries.Count > 0)
            {
                await SelectLibraryAsync(libraries[0].Id);
                return;
            }

            post(() =>
            {
                ActiveLibraryId = string.Empty;
                ActiveCollectionRef = null;
                Tracks.Clear();
                TotalCount = 0;
                HasMore = false;
                NavigationChanged?.Invoke();
            });
            return;
        }

        if (ActiveCollectionRef?.StartsWith("playlist:", StringComparison.Ordinal) == true)
        {
            var playlistId = ActiveCollectionRef["playlist:".Length..];
            if (!playlists.Any(playlist => playlist.Id == playlistId))
            {
                await SelectLibraryAsync(ActiveLibraryId);
                return;
            }
        }

        await RefreshNavigationAsync();
        if (IsGroupListVisible)
        {
            await LoadGroupSummaryAsync();
        }
        else
        {
            await LoadFirstPageAsync();
            if (IsArtistDetail)
                await LoadGroupSummaryAsync();
        }
    }
}
