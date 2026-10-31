using System.Collections.ObjectModel;
using System.Text.Json;
using OFPlayer.WinUI.Core.Remote;

namespace OFPlayer_WinUI.ViewModels;
/// <summary>Saved external-library connection (old front-end: external library connections).</summary>
public sealed record RemoteConnectionItem(string Id, string Name, string Provider, string Endpoint, JsonElement Raw);
/// <summary>A remote track row shown by the remote browser (kept raw for resolve).</summary>
public sealed record RemoteTrackItem(string Title, string Format, string DurationText, string SizeText, JsonElement Raw);
/// <summary>One selectable remote library reported by the provider (Subsonic music folder / WebDAV root).</summary>
public sealed record RemoteLibraryItem(string Id, string Name, RemoteLibraryDescriptor Descriptor)
{
    public override string ToString() => Name;
}

/// <summary>Draft fields edited in the "add remote source" dialog.</summary>
public sealed record RemoteConnectionDraft(string Name, string Provider, string Endpoint, string RootPath, string Username, string Password);
/// <summary>
/// D4 remote external libraries: connection CRUD, live probing, remote browsing and
/// playing a remote track through the resolve → local-cache → play pipeline.
/// All network work happens on the Rust host worker (never blocks the UI thread).
///
/// R4-4 additions: the provider's multiple libraries are surfaced and selectable
/// (the selection is carried on the connection's <c>rootPath</c>, which the host
/// maps to Subsonic's <c>musicFolderId</c>), the host's <c>truncated</c> flag is
/// reported honestly instead of being dropped, and every listing round is bound to
/// a <see cref = "RemoteRequestToken"/> so a late page from a previous library or
/// connection can never land in the current list.
/// </summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<RemoteConnectionItem> RemoteConnections { get; } = new();
    public ObservableCollection<RemoteTrackItem> RemoteTrackItems { get; } = new();
    /// <summary>R4-4: libraries reported by the active connection (empty until probed).</summary>
    public ObservableCollection<RemoteLibraryItem> RemoteLibraries { get; } = new();

    private bool isRemoteMode;
    private bool isRemoteBusy;
    private string externalPlaybackLabel = string.Empty;
    private string activeRemoteName = string.Empty;
    private string activeRemoteId = string.Empty;
    private string remoteStatusText = string.Empty;
    /// <summary>M3a: opening/refresh generations - a late A result must not overwrite B.</summary>
    private int remoteRequestGeneration;
    /// <summary>M3a: playback generations - a late A resolve must not start after B.</summary>
    private int remotePlayGeneration;
    /// <summary>R4-4: listing paging state (limit-growth only; the host has no cursor).</summary>
    private readonly RemotePagingState remotePaging = new();
    /// <summary>R4-4: token source so late pages are discarded by identity, not by name.</summary>
    private readonly RemoteRequestTokenSource remoteTokens = new();
    /// <summary>R4-4: the token of the most recent listing round.</summary>
    private RemoteRequestToken remoteActiveToken;
    /// <summary>R4-4: the library the active listing belongs to (empty = provider default).</summary>
    private string activeRemoteLibraryId = string.Empty;
    private RemoteLibraryItem? selectedRemoteLibrary;
    private bool canLoadMoreRemote;
    private bool isRemoteTruncated;
    public bool IsRemoteMode { get => isRemoteMode; private set => SetProperty(ref isRemoteMode, value); }
    public bool IsRemoteBusy { get => isRemoteBusy; private set => SetProperty(ref isRemoteBusy, value); }
    public string ActiveRemoteName { get => activeRemoteName; private set => SetProperty(ref activeRemoteName, value); }
    public string RemoteStatusText { get => remoteStatusText; private set => SetProperty(ref remoteStatusText, value); }

    /// <summary>R4-4: the library selector's current value (null = provider default).</summary>
    public RemoteLibraryItem? SelectedRemoteLibrary
    {
        get => selectedRemoteLibrary;
        set
        {
            // SetProperty returns void here (see MainViewModel core), so compare first.
            var changed = !ReferenceEquals(selectedRemoteLibrary, value);
            SetProperty(ref selectedRemoteLibrary, value);
            if (!changed || value is null)
            {
                return;
            }

            // Selecting a library restarts the listing against that library.
            _ = SelectRemoteLibraryAsync(value);
        }
    }

    /// <summary>R4-4: true when the host reported truncation and the cap is not yet reached.</summary>
    public bool CanLoadMoreRemote { get => canLoadMoreRemote; private set => SetProperty(ref canLoadMoreRemote, value); }
    /// <summary>R4-4: true when the host stopped at the requested limit.</summary>
    public bool IsRemoteTruncated { get => isRemoteTruncated; private set => SetProperty(ref isRemoteTruncated, value); }
    /// <summary>R4-4: how many libraries the active connection exposes (for the selector).</summary>
    public bool HasMultipleRemoteLibraries => RemoteLibraries.Count > 1;

    internal static JsonElement BuildConnectionValue(RemoteConnectionDraft draft, string? id = null)
    {
        var auth = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(draft.Username))
        {
            auth["username"] = draft.Username.Trim();
        }

        if (!string.IsNullOrWhiteSpace(draft.Password))
        {
            auth["password"] = draft.Password;
        }

        var value = new Dictionary<string, object?>
        {
            ["id"] = string.IsNullOrWhiteSpace(id) ? $"remote-{Guid.NewGuid():N}" : id,
            ["provider"] = draft.Provider.Trim().ToLowerInvariant(),
            ["name"] = draft.Name.Trim(),
            ["endpoint"] = draft.Endpoint.Trim(),
            ["rootPath"] = string.IsNullOrWhiteSpace(draft.RootPath) ? "/" : draft.RootPath.Trim(),
        };
        if (auth.Count > 0)
        {
            value["auth"] = auth;
        }

        return JsonSerializer.SerializeToElement(value);
    }

    public async Task LoadRemoteConnectionsAsync(int attempt = 0)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Remote.LoadRemoteConnectionsAsync");
        if (client is null)
        {
            return;
        }

        try
        {
            var result = await client.SendAsync("desktop_external_library_load_connections", null, CancellationToken.None);
            var items = new List<RemoteConnectionItem>();
            if (result.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in result.EnumerateArray())
                {
                    items.Add(new RemoteConnectionItem(ReadRemoteString(entry, "id") ?? string.Empty, ReadRemoteString(entry, "name") ?? ReadRemoteString(entry, "provider") ?? Services.Localization.Get("source.network"), ReadRemoteString(entry, "provider") ?? string.Empty, ReadRemoteString(entry, "endpoint") ?? string.Empty, entry.Clone()));
                }
            }

            RemoteConnections.Clear();
            foreach (var item in items)
            {
                RemoteConnections.Add(item);
            }
        }
        catch (Exception error)
        {
            if (attempt == 0)
            {
                // The first load can race the core handshake; retry once shortly.
                await Task.Delay(900);
                await LoadRemoteConnectionsAsync(1);
                return;
            }

            ReportCommandError(error);
        }
    }

    public async Task<bool> SaveRemoteConnectionAsync(RemoteConnectionDraft draft, string? existingId = null)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Remote.SaveRemoteConnectionAsync");
        if (client is null)
        {
            return false;
        }

        try
        {
            var value = BuildConnectionValue(draft, existingId);
            await client.SendAsync("desktop_external_library_put_connection", new { value }, CancellationToken.None);
            await LoadRemoteConnectionsAsync();
            RemoteStatusText = Services.Localization.Format("network.saved", ("name", draft.Name.Trim()));
            return true;
        }
        catch (Exception error)
        {
            ReportCommandError(error);
            return false;
        }
    }

    public async Task DeleteRemoteConnectionAsync(RemoteConnectionItem connection)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Remote.DeleteRemoteConnectionAsync");
        if (client is null)
        {
            return;
        }

        try
        {
            await client.SendAsync("desktop_external_library_delete_connection", new { connectionId = connection.Id }, CancellationToken.None);
            await LoadRemoteConnectionsAsync();
            if (IsRemoteMode && activeRemoteId == connection.Id)
            {
                CloseRemote();
            }

            RemoteStatusText = string.Empty;
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    /// <summary>Live probe against the real endpoint; returns the honest outcome.</summary>
    public async Task<(bool Ok, string Message)> TestRemoteConnectionAsync(RemoteConnectionDraft draft, JsonElement? rawConnection = null)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Remote.TestRemoteConnectionAsync");
        if (client is null)
        {
            return (false, Services.Localization.Get("status.notReady"));
        }

        try
        {
            var connection = rawConnection ?? BuildConnectionValue(draft);
            var result = await client.SendAsync("external_library_test_connection", new { request = new { connection } }, CancellationToken.None);
            if (result.ValueKind == JsonValueKind.Object)
            {
                var ok = result.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
                var message = ReadRemoteString(result, "message") ?? Services.Localization.Get(ok ? "network.test.ok" : "network.test.failed");
                return (ok, message);
            }

            return (false, Services.Localization.Get("network.test.failed"));
        }
        catch (Exception error)
        {
            return (false, error.Message);
        }
    }

    /// <summary>Probes and lists the remote tracks of a saved connection.</summary>
    public async Task OpenRemoteConnectionAsync(RemoteConnectionItem connection)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Remote.OpenRemoteConnectionAsync");
        if (client is null)
        {
            return;
        }

        // A->B race guard: opening B while A is still probing discards A's late
        // result instead of overwriting B's list (M3a).
        var generation = ++remoteRequestGeneration;
        IsRemoteBusy = true;
        RemoteStatusText = Services.Localization.Format("network.connecting", ("name", connection.Name));
        try
        {
            var probeResult = await client.SendAsync("external_library_test_connection", new { request = new { connection = connection.Raw } }, CancellationToken.None);
            if (generation != remoteRequestGeneration)
            {
                return;
            }

            var probeOk = probeResult.ValueKind == JsonValueKind.Object && probeResult.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
            if (!probeOk)
            {
                // The host's own message is an English sentence for the log; the listener gets a plain reason.
                Services.PreviewLog.Write($"network library probe failed: {ReadRemoteString(probeResult, "message")}");
                RemoteStatusText = Services.Localization.Format("network.connectFailed", ("reason", Services.Localization.Get("network.unreachable")));
                return;
            }

            // R4-4: the host already aggregates the provider's libraries
            // (Subsonic getMusicFolders, WebDAV single root). Surface them so the
            // user can pick one instead of always silently taking the default.
            var libraries = await FetchRemoteLibrariesAsync(connection);
            if (generation != remoteRequestGeneration)
            {
                return;
            }

            RemoteLibraries.Clear();
            foreach (var library in libraries)
            {
                RemoteLibraries.Add(library);
            }

            RaisePropertyChanged(nameof(HasMultipleRemoteLibraries));
            // Keep the previously chosen library when it still exists (refresh /
            // reconnect must not silently fall back to the provider default), and
            // otherwise let the provider decide by leaving the selection empty.
            var desiredLibraryId = activeRemoteId == connection.Id ? activeRemoteLibraryId : string.Empty;
            var selected = RemoteLibraries.FirstOrDefault(item => string.Equals(item.Id, desiredLibraryId, StringComparison.Ordinal));
            activeRemoteId = connection.Id;
            ActiveRemoteName = connection.Name;
            selectedRemoteLibrary = selected;
            RaisePropertyChanged(nameof(SelectedRemoteLibrary));
            await LoadRemoteTracksAsync(connection, selected?.Descriptor, isFirstPage: true);
        }
        catch (Exception error)
        {
            if (generation == remoteRequestGeneration)
            {
                RemoteStatusText = Services.Localization.Format("network.connectFailed", ("reason", Services.FailureText.Reason(error, "network library connect failed")));
            }
        }
        finally
        {
            if (generation == remoteRequestGeneration)
            {
                IsRemoteBusy = false;
            }
        }
    }

    /// <summary>
    /// R4-4: one listing round. <paramref name = "descriptor"/> selects the library by
    /// overriding the connection's <c>rootPath</c> (the host maps that to Subsonic's
    /// <c>musicFolderId</c>); <c>null</c> keeps the connection's own root.
    ///
    /// Every round is bound to a token: the result is applied only if that token is
    /// still current, so a late page for a previous library/connection is discarded
    /// even when the two libraries happen to share a name.
    /// </summary>
    private async Task LoadRemoteTracksAsync(RemoteConnectionItem connection, RemoteLibraryDescriptor? descriptor, bool isFirstPage, int? grownLimit = null)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Remote.LoadRemoteTracksAsync");
        if (client is null)
        {
            return;
        }

        var libraryId = descriptor?.Id ?? string.Empty;
        var token = remoteTokens.Issue(connection.Id, libraryId);
        remoteActiveToken = token;
        activeRemoteLibraryId = libraryId;
        var limit = isFirstPage ? remotePaging.Reset() : grownLimit ?? remotePaging.RequestedLimit;
        if (isFirstPage)
        {
            remotePaging.PageSize = RemotePagingState.DefaultPageSize;
        }

        IsRemoteBusy = true;
        try
        {
            var requestConnection = descriptor is { } value ? ApplyLibraryRootPath(connection.Raw, value) : connection.Raw;
            var result = await client.SendAsync("external_library_list_tracks", new { request = new { connection = requestConnection, limit } }, CancellationToken.None);
            if (IsRemoteTokenStale(token))
            {
                return;
            }

            var items = new List<RemoteTrackItem>();
            var providerTruncated = false;
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in tracks.EnumerateArray())
                {
                    items.Add(new RemoteTrackItem(ReadRemoteString(entry, "title") ?? ReadRemoteString(entry, "fileName") ?? Services.Localization.Get("track.untitled"), (ReadRemoteString(entry, "format") ?? GuessFormat(entry)).ToUpperInvariant(), FormatDuration(ReadNumber(entry, "durationSeconds")), FormatSize(ReadNumber(entry, "sizeBytes") ?? ReadNumber(entry, "fileSize")), entry.Clone()));
                }
            }

            // The host's own truncation flag is authoritative. The previous code
            // hardcoded limit=500 and dropped this flag entirely, so a large library
            // looked complete while silently missing everything past the cut.
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("diagnostics", out var diagnostics) && diagnostics.ValueKind == JsonValueKind.Object && diagnostics.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            {
                providerTruncated = true;
            }

            var previousCount = RemoteTrackItems.Count;
            // Replace rather than append: with no cursor the provider re-sends its
            // whole listing, so its order stays authoritative and a shrink (remote
            // files deleted) is reflected instead of leaving a stale prefix behind.
            RemoteTrackItems.Clear();
            foreach (var item in items)
            {
                RemoteTrackItems.Add(item);
            }

            remotePaging.Apply(limit, items.Count, providerTruncated);
            IsRemoteTruncated = remotePaging.HasMore;
            CanLoadMoreRemote = remotePaging.HasMore && !remotePaging.CapReached;
            var stalled = !isFirstPage && RemoteTrackMerge.MadeNoProgress(previousCount, items.Count, providerTruncated);
            RemoteStatusText = BuildRemoteStatusText(items.Count, providerTruncated, stalled, descriptor);
            IsRemoteMode = true;
        }
        catch (Exception error)
        {
            if (!IsRemoteTokenStale(token))
            {
                // Honest, actionable failure text: the retry path is the refresh button.
                RemoteStatusText = Services.Localization.Format("network.loadFailed", ("reason", Services.FailureText.Reason(error, "network library load failed")));
            }
        }
        finally
        {
            if (!IsRemoteTokenStale(token))
            {
                IsRemoteBusy = false;
            }
        }
    }

    private string BuildRemoteStatusText(int count, bool providerTruncated, bool stalled, RemoteLibraryDescriptor? descriptor)
    {
        if (count == 0)
        {
            return descriptor is { } library && !string.IsNullOrWhiteSpace(library.Name) ? Services.Localization.Format("network.empty.folder", ("name", library.Name)) : Services.Localization.Get("network.empty");
        }

        var songs = ("count", count.ToString("N0", OFPlayer_WinUI.Services.Localization.Culture));
        // The server stopped at the same number, or the request is as large as it can get:
        // more requests will not show more.
        if (stalled || (providerTruncated && remotePaging.CapReached))
        {
            return Services.Localization.Format("network.loaded.limit", songs);
        }

        return Services.Localization.Format(providerTruncated ? "network.loaded.more" : "network.loaded", songs);
    }

    /// <summary>R4-4: selects a library and restarts the listing against it.</summary>
    public async Task SelectRemoteLibraryAsync(RemoteLibraryItem library)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Remote.SelectRemoteLibraryAsync");
        var connection = ActiveRemoteConnection();
        if (connection is null || library is null)
        {
            return;
        }

        RemoteStatusText = Services.Localization.Format("network.loading.folder", ("name", library.Name));
        await LoadRemoteTracksAsync(connection, library.Descriptor, isFirstPage: true);
    }

    /// <summary>
    /// R4-4: "load more". The host contract has no offset/cursor, so the only honest
    /// increment is a larger <c>limit</c>. Growth is capped so a provider that always
    /// reports truncation cannot make the client hammer it forever.
    /// </summary>
    public async Task LoadMoreRemoteAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Remote.LoadMoreRemoteAsync");
        var connection = ActiveRemoteConnection();
        if (connection is null)
        {
            RemoteStatusText = Services.Localization.Get("network.gone");
            return;
        }

        var next = remotePaging.Grow();
        if (next is null)
        {
            CanLoadMoreRemote = false;
            RemoteStatusText = remotePaging.CapReached ? Services.Localization.Get("network.truncated") : Services.Localization.Get("network.noMore");
            return;
        }

        var descriptor = selectedRemoteLibrary?.Descriptor ?? (activeRemoteLibraryId.Length > 0 ? RemoteLibraryDescriptor.FromHost(activeRemoteLibraryId, string.Empty, activeRemoteLibraryId) : (RemoteLibraryDescriptor? )null);
        RemoteStatusText = Services.Localization.Get("network.loadingMore");
        await LoadRemoteTracksAsync(connection, descriptor, isFirstPage: false, grownLimit: next);
    }

    private async Task<List<RemoteLibraryItem>> FetchRemoteLibrariesAsync(RemoteConnectionItem connection)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Remote.FetchRemoteLibrariesAsync");
        var libraries = new List<RemoteLibraryItem>();
        try
        {
            var result = await client!.SendAsync("external_library_list_libraries", new { request = new { connection = connection.Raw } }, CancellationToken.None);
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("libraries", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in array.EnumerateArray())
                {
                    var descriptor = RemoteLibraryDescriptor.FromHost(ReadRemoteString(entry, "id"), ReadRemoteString(entry, "name"), ReadRemoteString(entry, "rootPath"));
                    if (descriptor.IsValid)
                    {
                        libraries.Add(new RemoteLibraryItem(descriptor.Id, descriptor.Name, descriptor));
                    }
                }
            }
        }
        catch (Exception error)
        {
            // Listing libraries is optional for browsing: FTP and bare WebDAV roots
            // legitimately answer with an error. Fall through to the connection root.
            OFPlayer_WinUI.Services.PreviewLog.Write($"remote libraries failed: {error.GetType().Name}: {error.Message}");
        }

        return libraries;
    }

    /// <summary>
    /// Rebuilds the connection JSON with the chosen library's root path. Kept as a
    /// clone: the stored connection must not be mutated by a browse operation.
    /// </summary>
    private static JsonElement ApplyLibraryRootPath(JsonElement connection, RemoteLibraryDescriptor descriptor)
    {
        var value = new Dictionary<string, object?>();
        if (connection.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in connection.EnumerateObject())
            {
                value[property.Name] = property.Value.Clone();
            }
        }

        value["rootPath"] = descriptor.ConnectionRootPath;
        return JsonSerializer.SerializeToElement(value);
    }

    private bool IsRemoteTokenStale(RemoteRequestToken token) => token.IsStaleFor(remoteActiveToken) || token.Generation != remoteTokens.Current;
    /// <summary>
    /// R4-4: name is only a fallback. Resolving the active connection by name used to
    /// be the *only* path, so two saved connections sharing a display name made the
    /// remote view act on the wrong one. Id is authoritative now, and a connection
    /// with an empty id can never be picked by accident.
    /// </summary>
    private RemoteConnectionItem? ActiveRemoteConnection()
    {
        if (!string.IsNullOrWhiteSpace(activeRemoteId))
        {
            var byId = RemoteConnections.FirstOrDefault(item => string.Equals(item.Id, activeRemoteId, StringComparison.Ordinal));
            if (byId is not null)
            {
                return byId;
            }
        }

        // Fallback only when the id is unknown (e.g. the saved list was reloaded and
        // this entry was renamed). Ambiguous names resolve to nothing rather than to
        // an arbitrary match.
        if (string.IsNullOrWhiteSpace(ActiveRemoteName))
        {
            return null;
        }

        var byName = RemoteConnections.Where(item => string.Equals(item.Name, ActiveRemoteName, StringComparison.Ordinal)).Take(2).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    /// <summary>M3a: re-runs the listing for the active connection (also the retry path).</summary>
    public async Task RefreshRemoteAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Remote.RefreshRemoteAsync");
        var connection = ActiveRemoteConnection();
        if (connection is null)
        {
            RemoteStatusText = Services.Localization.Get("network.gone");
            return;
        }

        // Keep the current library selection across a refresh (OpenRemoteConnectionAsync
        // preserves activeRemoteLibraryId when the connection is unchanged).
        await OpenRemoteConnectionAsync(connection);
    }

    public void CloseRemote()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Remote.CloseRemote");
        // Invalidate any in-flight open/refresh so it cannot resurrect the view.
        remoteRequestGeneration++;
        // R4-4: also drop outstanding listing tokens and paging state so a page that
        // is still on the wire cannot land after the view was closed.
        remoteTokens.Invalidate();
        remoteActiveToken = default;
        remotePaging.Reset();
        IsRemoteMode = false;
        IsRemoteTruncated = false;
        CanLoadMoreRemote = false;
        RemoteTrackItems.Clear();
        RemoteLibraries.Clear();
        RaisePropertyChanged(nameof(HasMultipleRemoteLibraries));
        selectedRemoteLibrary = null;
        RaisePropertyChanged(nameof(SelectedRemoteLibrary));
        activeRemoteLibraryId = string.Empty;
        activeRemoteId = string.Empty;
        ActiveRemoteName = string.Empty;
        RemoteStatusText = string.Empty;
    }

    /// <summary>Resolve → downloaded local cache → play (old playback_load_track pipeline).</summary>
    public async Task PlayRemoteTrackAsync(RemoteTrackItem track, JsonElement? connectionRaw = null)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Remote.PlayRemoteTrackAsync");
        if (client is null)
        {
            return;
        }

        var connection = connectionRaw ?? ActiveRemoteConnection()?.Raw;
        if (connection is null)
        {
            RemoteStatusText = Services.Localization.Get("network.gone");
            return;
        }

        // A->B race guard: a late resolve for A must not start after B took over.
        var generation = ++remotePlayGeneration;
        try
        {
            RemoteStatusText = Services.Localization.Format("network.preparing", ("title", track.Title));
            var resolved = await client.SendAsync("external_library_resolve_playback_source", new { request = new { connection, track = track.Raw, includeMetadata = true } }, CancellationToken.None);
            if (generation != remotePlayGeneration)
            {
                return;
            }

            var path = resolved.ValueKind == JsonValueKind.Object && resolved.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object ? ReadRemoteString(source, "path") : null;
            if (string.IsNullOrWhiteSpace(path))
            {
                RemoteStatusText = Services.Localization.Get("network.playFailed.source");
                return;
            };
            await client.SendAsync("playback_play_external", new { path, title = track.Title, artist = ActiveRemoteName, connectionId = ReadRemoteString(connection.Value, "id") }, CancellationToken.None);
            if (generation != remotePlayGeneration)
            {
                return;
            }

            externalPlaybackLabel = string.IsNullOrEmpty(ActiveRemoteName) ? track.Title : $"{track.Title} · {ActiveRemoteName}";
            RemoteStatusText = Services.Localization.Format("network.playing", ("title", track.Title));
        }
        catch (Exception error)
        {
            if (generation == remotePlayGeneration)
            {
                RemoteStatusText = Services.Localization.Format("network.playFailed", ("reason", Services.FailureText.Reason(error, "network library play failed")));
            }
        }
    }

    private static string GuessFormat(JsonElement entry)
    {
        var fileName = ReadRemoteString(entry, "fileName") ?? ReadRemoteString(entry, "path") ?? string.Empty;
        var dot = fileName.LastIndexOf('.');
        return dot >= 0 && dot < fileName.Length - 1 ? fileName[(dot + 1)..] : "audio";
    }

    private static string FormatDuration(double? seconds) => seconds is> 0 ? TimeSpan.FromSeconds(seconds.Value).ToString(@"m\:ss") : "—";
    private static string FormatSize(double? bytes) => bytes is> 0 ? bytes.Value >= 1024 * 1024 ? $"{bytes.Value / (1024 * 1024):0.0} MB" : $"{bytes.Value / 1024:0} KB" : "—";
    private static string? ReadRemoteString(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? ReadNumber(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
