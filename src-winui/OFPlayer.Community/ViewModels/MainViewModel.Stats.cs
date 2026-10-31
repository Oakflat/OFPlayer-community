using OFPlayer_WinUI.Models;

namespace OFPlayer_WinUI.ViewModels;
/// <summary>
/// R3 UI17: full listening-stats snapshot loader for the stats dialog
/// (the settings summary line keeps using ListeningStatsText).
/// </summary>
public sealed partial class MainViewModel
{
    public async Task<ListeningStatsData?> LoadStatsSnapshotAsync(int days = 120)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Stats.LoadStatsSnapshotAsync");
        if (client is null)
        {
            return null;
        }

        try
        {
            // Pass the selected range and local offset so calendar boundaries
            // agree with the host aggregation.
            var offsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;
            var result = await client.SendAsync("desktop_history_load_stats", new { request = new { days, timezoneOffsetMinutes = offsetMinutes } }, CancellationToken.None);
            return ListeningStatsData.Parse(result);
        }
        catch (Exception error)
        {
            ReportCommandError(error);
            return null;
        }
    }
}
