using System.Text.Json;
using OFPlayer_WinUI.Models;
using OFPlayer_WinUI.Services;

namespace OFPlayer_WinUI.ViewModels;
/// <summary>
/// D1 shell state: source picker labels, track inspector (selected vs now playing),
/// and the layout tier reported by the window for reporting/diagnostics.
/// </summary>
public sealed partial class MainViewModel
{
    private const int InspectorDetailCacheSize = 8;
    private TrackDetail? selectedTrackDetail;
    private string? selectedTrackId;
    private bool selectedTrackIsFavorite;
    private readonly Dictionary<string, TrackDetail> inspectorDetailCache = new();
    private readonly List<string> inspectorDetailOrder = new();
    private bool isInspectorOpen = true;
    private string layoutTier = "wide";
    /// <summary>Raised when the source picker activates a library (MainWindow re-selects navigation).</summary>
    public event Action<string>? LibraryActivated;
    /// <summary>Detail of the row selected in the list (null: fall back to now playing).</summary>
    public TrackDetail? SelectedTrackDetail { get => selectedTrackDetail; private set => SetProperty(ref selectedTrackDetail, value); }
    public bool HasSelectedTrack => selectedTrackDetail is not null;
    public bool SelectedTrackIsFavorite { get => selectedTrackIsFavorite; private set => SetProperty(ref selectedTrackIsFavorite, value); }
    public bool IsInspectorOpen { get => isInspectorOpen; private set => SetProperty(ref isInspectorOpen, value); }
    public string LayoutTier => layoutTier;

    /// <summary>Source picker label: active library name plus the local/remote identity.</summary>
    public string SourceDisplayName
    {
        get
        {
            var library = libraries.Find(entry => entry.Id == activeLibraryId);
            return library is null ? Localization.Get("source.choose") : LibraryDisplayName(library);
        }
    }

    private static string ViewTitle(string viewKey) => Localization.Get(viewKey switch
    {
        "all-favorites" => "nav.favorites",
        "all-plays" => "nav.recentPlays",
        "recent-imports" => "nav.recentImports",
        "current-queue" => "nav.queue",
        "albums" => "nav.albums",
        "artists" => "nav.artists",
        _ => "nav.library",
    });
    /// <summary>
    /// Re-reads the names this model gives to the library's own collections (page
    /// title, play mode, output) after a language change. Names that come from the
    /// user or the catalog (playlists, albums, artists) are left as they are.
    /// </summary>
    public void RefreshLocalizedText()
    {
        if (activeGroupKey is null && parentArtist is null && ActiveCollectionRef is { } collection)
        {
            if (collection.StartsWith("view:", StringComparison.Ordinal))
                PageTitle = ViewTitle(collection["view:".Length..]);
            else if (collection.StartsWith("playlist:", StringComparison.Ordinal) && playlists.FirstOrDefault(node => node.Id == collection["playlist:".Length..])is { SystemKey: "all-tracks" })
                PageTitle = Localization.Get("nav.allSongs");
        }

        if (!IsConnected && !IsBusy && StatusKind == StatusKind.Informational)
            StatusMessage = Localization.Get("status.starting");
        ConnectButtonText = Localization.Get("common.retry");
        RaisePropertyChanged(nameof(GroupBackLabel));
        RaisePropertyChanged(nameof(PlayModeLabel));
        RaisePropertyChanged(nameof(OutputDeviceLabel));
        RaisePropertyChanged(nameof(PlaybackLabel));
        RaisePropertyChanged(nameof(DataRootText));
        RefreshStorageLocalizedText();
    }

    /// <summary>The default library has an intentionally empty stored name until renamed.</summary>
    public static string LibraryDisplayName(LibraryNode library) => OFPlayer.WinUI.Core.Catalog.LibraryDisplayName.Resolve(library.Id, library.Name, library.IsDefault, Localization.Get("sidebar.libraries.local"), Localization.Get("sidebar.librarySection")) + (library.IsLocked ? " · " + Localization.Get("quota.lockedLabel") : string.Empty);
    public void SetLayoutTier(string tier)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Shell.SetLayoutTier");
        if (tier == layoutTier)
        {
            return;
        }

        layoutTier = tier;
        RaisePropertyChanged(nameof(LayoutTier));
    }

    public void ToggleInspector() => IsInspectorOpen = !IsInspectorOpen;
    public void CloseInspector() => IsInspectorOpen = false;
    public void ActivateLibrary(string libraryId)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Shell.ActivateLibrary");
        if (!string.IsNullOrWhiteSpace(libraryId))
        {
            LibraryActivated?.Invoke(libraryId);
        }
    }

    /// <summary>Loads the inspector detail for the currently selected row (old front-end: selection info).</summary>
    public async Task SetSelectedRowAsync(TrackQueryRow? row)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Shell.SetSelectedRowAsync");
        var trackId = row?.Id;
        if (string.IsNullOrEmpty(trackId))
        {
            selectedTrackId = null;
            SelectedTrackDetail = null;
            RaisePropertyChanged(nameof(HasSelectedTrack));
            return;
        }

        if (trackId != selectedTrackId)
        {
            selectedTrackId = trackId;
            SelectedTrackIsFavorite = row!.Favorite;
        }

        if (inspectorDetailCache.TryGetValue(trackId, out var cached))
        {
            SelectedTrackDetail = cached;
            RaisePropertyChanged(nameof(HasSelectedTrack));
            return;
        }

        try
        {
            var activeClient = client;
            if (activeClient is null)
            {
                return;
            }

            var result = await activeClient.SendAsync("desktop_catalog_get_track", new { trackId, includeArtwork = true }, CancellationToken.None);
            if (result.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var detail = TrackDetail.FromJson(result);
            if (selectedTrackId != trackId)
            {
                return;
            }

            inspectorDetailCache[trackId] = detail;
            inspectorDetailOrder.Add(trackId);
            while (inspectorDetailOrder.Count > InspectorDetailCacheSize)
            {
                inspectorDetailCache.Remove(inspectorDetailOrder[0]);
                inspectorDetailOrder.RemoveAt(0);
            }

            post(() =>
            {
                SelectedTrackDetail = detail;
                RaisePropertyChanged(nameof(HasSelectedTrack));
            });
        }
        catch (Exception error)
        {
            PreviewLog.Write($"inspector detail failed: {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>Favorite toggle from the inspector (explicit track; refreshes the visible page).</summary>
    public async Task ToggleInspectorFavoriteAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Shell.ToggleInspectorFavoriteAsync");
        var trackId = selectedTrackId;
        if (client is null || string.IsNullOrEmpty(trackId))
        {
            return;
        }

        var next = !selectedTrackIsFavorite;
        try
        {
            await client.SendAsync("desktop_catalog_update_track", new { trackId, patch = new { isFavorite = next } }, CancellationToken.None);
            SelectedTrackIsFavorite = next;
            // Refresh the visible page so list stars follow the toggle.
            await LoadPageAsync(0, append: false);
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    /// <summary>D2: marks the row matching the active track (selection vs playing stay distinct).</summary>
    private void UpdateActiveRow(string? activeTrackId)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Shell.UpdateActiveRow");
        for (var index = 0; index < Tracks.Count; index++)
        {
            var row = Tracks[index];
            var shouldBeActive = activeTrackId is not null && row.Id == activeTrackId;
            if (row.IsActive != shouldBeActive)
            {
                // Preserve item/container identity: replacing the row triggers list
                // remove/add animations, drops selection and reloads its artwork.
                row.IsActive = shouldBeActive;
            }
        }
    }

    /// <summary>D2: row-level artwork; data URIs remain inspector-only (row decode cost).</summary>
    private static Microsoft.UI.Xaml.Media.ImageSource? BuildRowArtwork(string? artwork)
    {
        if (string.IsNullOrWhiteSpace(artwork) || artwork.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            return System.IO.File.Exists(artwork) ? new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(artwork)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Re-queries the visible page after a row mutation (favorite etc.).</summary>
    public Task RefreshVisiblePageAsync() => LoadPageAsync(0, append: false);
    // ---- D3: settings page support ----
    private IReadOnlyList<string> lyricsScanDirectories = Array.Empty<string>();
    /// <summary>D3: lyrics scan directories (preferences-backed, managed in settings).</summary>
    public IReadOnlyList<string> LyricsScanDirectories => lyricsScanDirectories;

    public async Task RefreshLyricsScanDirectoriesAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Shell.RefreshLyricsScanDirectoriesAsync");
        if (client is null)
        {
            return;
        }

        try
        {
            var preferences = await client.SendAsync("desktop_preferences_load", null, CancellationToken.None);
            var directories = new List<string>();
            if (preferences.ValueKind == JsonValueKind.Object && preferences.TryGetProperty("lyricsScanDirectories", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in array.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.String)
                    {
                        var text = entry.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            directories.Add(text);
                        }
                    }
                }
            }

            post(() =>
            {
                lyricsScanDirectories = directories;
                RaisePropertyChanged(nameof(LyricsScanDirectories));
            });
        }
        catch (Exception error)
        {
            PreviewLog.Write($"scan dirs load failed: {error.GetType().Name}: {error.Message}");
        }
    }

    public async Task AddLyricsScanDirectoryAsync(string directory)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Shell.AddLyricsScanDirectoryAsync");
        if (client is null || string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var next = lyricsScanDirectories.ToList();
        if (!next.Contains(directory))
        {
            next.Add(directory);
        }

        await SaveLyricsScanDirectoriesAsync(next);
    }

    public async Task RemoveLyricsScanDirectoryAsync(string directory)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Shell.RemoveLyricsScanDirectoryAsync");
        if (client is null)
        {
            return;
        }

        var next = lyricsScanDirectories.Where(entry => entry != directory).ToList();
        await SaveLyricsScanDirectoriesAsync(next);
    }

    private async Task SaveLyricsScanDirectoriesAsync(IReadOnlyList<string> directories)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Shell.SaveLyricsScanDirectoriesAsync");
        if (client is null)
        {
            return;
        }

        try
        {
            await client.SendAsync("desktop_preferences_save", new { patch = new { lyricsScanDirectories = directories } }, CancellationToken.None);
            await RefreshLyricsScanDirectoriesAsync();
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }
}
