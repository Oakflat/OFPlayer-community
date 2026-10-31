using OFPlayer_WinUI.Models;
using OFPlayer_WinUI.Services;

namespace OFPlayer_WinUI.ViewModels;
public sealed partial class MainViewModel
{
    private readonly SemaphoreSlim localFileChecks = new(2, 2);
    public event Action<TrackQueryRow>? MissingFileRequested;
    private static bool PathIsMissing(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        } // Unreachable is not proof of deletion.
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task CheckLocalFilesAsync(IReadOnlyList<TrackQueryRow> rows, CancellationToken token)
    {
        var activeClient = client;
        if (activeClient is null || IsRemoteMode || rows.Count == 0)
            return;
        try
        {
            foreach (var batch in rows.Chunk(256))
            {
                var paths = await activeClient.SendAsync("desktop_catalog_local_paths", new { trackIds = batch.Select(r => r.Id).ToArray() }, token);
                var byId = batch.ToDictionary(r => r.Id);
                var checks = new List<Task>();
                foreach (var item in paths.EnumerateArray())
                {
                    var id = item.GetProperty("id").GetString()!;
                    var path = item.GetProperty("path").GetString()!;
                    if (byId.TryGetValue(id, out var row))
                        checks.Add(CheckAsync(row, path));
                }

                await Task.WhenAll(checks);
            }

            async Task CheckAsync(TrackQueryRow row, string path)
            {
                await localFileChecks.WaitAsync(token);
                try
                {
                    var missing = await Task.Run(() => PathIsMissing(path), token);
                    post(() =>
                    {
                        if (closing || token.IsCancellationRequested || activeClient != client || !Tracks.Any(r => ReferenceEquals(r, row)))
                            return;
                        row.LocalPath = path;
                        row.IsFileMissing = missing;
                    });
                }
                finally
                {
                    localFileChecks.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            PreviewLog.Write($"local file status check failed: {error.GetType().Name}");
        }
    }

    /// <summary>Where these library songs' files are on this computer, by track id. Songs without one are left out.</summary>
    public async Task<IReadOnlyDictionary<string, string>> LocalPathsAsync(IReadOnlyList<string> trackIds)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (client is not { } activeClient || IsRemoteMode)
            return found;
        foreach (var batch in trackIds.Chunk(256))
        {
            var paths = await activeClient.SendAsync("desktop_catalog_local_paths", new { trackIds = batch }, CancellationToken.None);
            foreach (var item in paths.EnumerateArray())
                if (item.GetProperty("path").GetString()is { Length: > 0 } path)
                    found[item.GetProperty("id").GetString()!] = path;
        }

        return found;
    }

    private async Task<bool> OfferMissingFileRecoveryAsync(TrackQueryRow row)
    {
        if (client is null)
            return false;
        if (string.IsNullOrEmpty(row.LocalPath))
        {
            var paths = await client.SendAsync("desktop_catalog_local_paths", new { trackIds = new[] { row.Id } }, CancellationToken.None);
            foreach (var item in paths.EnumerateArray())
                row.LocalPath = item.GetProperty("path").GetString();
        }

        if (string.IsNullOrEmpty(row.LocalPath))
            return false;
        var missing = await Task.Run(() => PathIsMissing(row.LocalPath));
        row.IsFileMissing = missing;
        if (missing)
            post(() => MissingFileRequested?.Invoke(row));
        return missing;
    }

    public async Task RelinkLocalFileAsync(TrackQueryRow row, string path)
    {
        var activeClient = client;
        if (activeClient is null || string.IsNullOrEmpty(row.LocalPath))
            return;
        try
        {
            await activeClient.SendAsync("desktop_catalog_relink_local_track", new { trackId = row.Id, previousPath = row.LocalPath, path }, CancellationToken.None);
            if (activeClient != client)
                return;
            trackDetailCache.Remove(row.Id);
            row.LocalPath = path;
            row.IsFileMissing = false;
            await RefreshAfterMutationAsync(Localization.Get("missing.file.restored"));
        }
        catch (Exception error)
        {
            StatusKind = StatusKind.Warning;
            StatusMessage = Localization.Get(error.Message.Contains("LOCAL_FILE_ALREADY_INDEXED") ? "missing.file.duplicate" : error.Message.Contains("LOCAL_TRACK_CHANGED") ? "missing.file.changed" : "missing.file.failed");
            PreviewLog.Write($"file relink failed: {error.GetType().Name}: {error.Message}");
        }
    }
}
