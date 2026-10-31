using System.Text.Json;

namespace OFPlayer_WinUI.ViewModels;
public sealed partial class MainViewModel
{
    public Task<JsonElement> LoadStorageAnalysisAsync() => SendStorageRequestAsync("desktop_storage_analyze");
    public Task<JsonElement> RunStorageCleanupAsync() => SendStorageRequestAsync("desktop_storage_collect_garbage");
    private Task<JsonElement> SendStorageRequestAsync(string command)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Insights.SendStorageRequestAsync");
        if (client is null || !IsConnected)
            throw new InvalidOperationException(OFPlayer_WinUI.Services.Localization.Get("core.notConnected"));
        return client.SendAsync(command, null, CancellationToken.None);
    }
}
