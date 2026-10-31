using System.Text.Json;

namespace OFPlayer_WinUI.ViewModels;
public sealed partial class MainViewModel
{
    private readonly Dictionary<string, Task<string?>> rowArtworkRequests = new();
    private readonly SemaphoreSlim artworkLookupSlots = new(4);
    public Task<string?> LoadRowArtworkAsync(string trackId)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Artwork.LoadRowArtworkAsync");
        if (rowArtworkRequests.TryGetValue(trackId, out var pending))
            return pending;
        if (rowArtworkRequests.Count >= 256)
            rowArtworkRequests.Clear();
        return rowArtworkRequests[trackId] = ReadRowArtworkAsync(trackId);
    }

    private async Task<string?> ReadRowArtworkAsync(string trackId)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Artwork.ReadRowArtworkAsync");
        await artworkLookupSlots.WaitAsync();
        try
        {
            if (client is null)
                return null;
            var result = await client.SendAsync("desktop_catalog_get_track", new { trackId, includeArtwork = true }, CancellationToken.None);
            return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("artwork", out var artwork) && artwork.ValueKind == JsonValueKind.String ? artwork.GetString() : null;
        }
        catch (Exception error)
        {
            Services.PreviewLog.Write($"row artwork lookup failed: {error.GetType().Name}");
            return null;
        }
        finally
        {
            artworkLookupSlots.Release();
        }
    }
}
