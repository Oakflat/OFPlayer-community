namespace OFPlayer_WinUI.ViewModels;
public sealed partial class MainViewModel
{
    internal const string DefaultSmartPlaylists = "expanded";
    private string smartPlaylistsPreference = DefaultSmartPlaylists;
    /// <summary>
    /// How the sidebar shows a library's smart playlists (all songs, albums, artists, favorites, recent
    /// plays, recent additions, the queue): "expanded" lists them in the sidebar, "collapsed" holds them in
    /// one row that opens downward when pointed at or clicked. Stored with the other preferences.
    /// </summary>
    public string SmartPlaylistsPreference => smartPlaylistsPreference;

    private static string NormalizeSmartPlaylists(string? value) => value is "expanded" or "collapsed" ? value : DefaultSmartPlaylists;
    public async Task SetSmartPlaylistsAsync(string mode)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.SidebarPreference.SetSmartPlaylistsAsync");
        var next = NormalizeSmartPlaylists(mode);
        if (next == smartPlaylistsPreference)
            return;
        smartPlaylistsPreference = next;
        post(() => RaisePropertyChanged(nameof(SmartPlaylistsPreference)));
        await PersistPreferencesAsync(new { smartPlaylists = smartPlaylistsPreference });
    }
}
