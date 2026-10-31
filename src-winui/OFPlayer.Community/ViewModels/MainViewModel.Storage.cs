using System.Text.Json;
using OFPlayer_WinUI.Services;

namespace OFPlayer_WinUI.ViewModels;
/// <summary>
/// M1 local-daily features: listening-stats summary, storage maintenance
/// (garbage collection) and the guarded full-data reset (the UI confirms first;
/// reset stays blocked while an import worker is running).
/// </summary>
public sealed partial class MainViewModel
{
    private string listeningStatsText = string.Empty;
    private string storageMaintenanceStatus = string.Empty;
    private readonly Dictionary<string, Func<string>> storageDisplayText = new(StringComparer.Ordinal);
    // Cache presentation functions only; switching languages must not repeat scans,
    // uploads, resets or diagnostic queries.
    private void SetStorageDisplayText(string property, Func<string> text)
    {
        storageDisplayText[property] = text;
        ApplyStorageDisplayText(property, text());
    }

    public void RefreshStorageLocalizedText()
    {
        foreach (var entry in storageDisplayText.ToArray())
            ApplyStorageDisplayText(entry.Key, entry.Value());
    }

    private void ApplyStorageDisplayText(string property, string text)
    {
        switch (property)
        {
            case nameof(ListeningStatsText):
                ListeningStatsText = text;
                break;
            case nameof(StorageMaintenanceStatus):
                StorageMaintenanceStatus = text;
                break;
            case nameof(ManualLibraryScanStatusText):
                ManualLibraryScanStatusText = text;
                break;
            case nameof(DiagnosticsStatusText):
                DiagnosticsStatusText = text;
                break;
            case nameof(DiagnosticsUploadStatusText):
                DiagnosticsUploadStatusText = text;
                break;
        }
    }

    public string ListeningStatsText { get => listeningStatsText; private set => SetProperty(ref listeningStatsText, value); }
    public string StorageMaintenanceStatus { get => storageMaintenanceStatus; private set => SetProperty(ref storageMaintenanceStatus, value); }
    /// <summary>Reset stays blocked while an import worker is running.</summary>
    public bool CanReset => IsConnected && !IsImporting;

    public async Task LoadListeningStatsAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Storage.LoadListeningStatsAsync");
        if (client is null)
        {
            return;
        }

        try
        {
            var result = await client.SendAsync("desktop_history_load_stats", new { request = new { } }, CancellationToken.None);
            var stats = result.Clone();
            post(() => SetStorageDisplayText(nameof(ListeningStatsText), () => BuildStatsSummary(stats)));
        }
        catch (Exception error)
        {
            ReportCommandError(error);
            var reasonKey = FailureText.Key(error, "listening stats failed");
            post(() => SetStorageDisplayText(nameof(ListeningStatsText), () => Localization.Format("stats.summary.failed", ("reason", Localization.Get(reasonKey)))));
        }
    }

    private static string BuildStatsSummary(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Object)
        {
            return Localization.Get("stats.summary.empty");
        }

        var lines = new List<string>();
        var playCount = ReadStatsInt(summary, "playCount");
        var totalMinutes = ReadStatsNumber(summary, "totalSeconds") / 60.0;
        var trackCount = ReadStatsInt(summary, "trackCount");
        var albumCount = ReadStatsInt(summary, "albumCount");
        var activeDays = ReadStatsInt(summary, "activeDays");
        var streak = ReadStatsInt(summary, "longestStreakDays");
        lines.Add(Localization.Format("stats.summary.plays", ("count", playCount.ToString("N0", Localization.Culture)), ("minutes", totalMinutes.ToString("0.#", Localization.Culture))));
        lines.Add(Localization.Format("stats.summary.collection", ("tracks", trackCount.ToString("N0", Localization.Culture)), ("albums", albumCount.ToString("N0", Localization.Culture)), ("days", activeDays.ToString("N0", Localization.Culture)), ("streak", streak.ToString("N0", Localization.Culture))));
        if (result.TryGetProperty("topTracks", out var topTracks) && topTracks.ValueKind == JsonValueKind.Array && topTracks.GetArrayLength() > 0)
        {
            var top = topTracks[0];
            var title = ReadStatsText(top, "title");
            var artist = ReadStatsText(top, "artist");
            var topPlays = ReadStatsInt(top, "playCount");
            if (!string.IsNullOrEmpty(title))
            {
                lines.Add(Localization.Format("stats.summary.top", ("track", title + (artist.Length > 0 ? $" · {artist}" : string.Empty)), ("count", topPlays.ToString("N0", Localization.Culture))));
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    public async Task CollectStorageGarbageAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Storage.CollectStorageGarbageAsync");
        if (client is null)
        {
            return;
        }

        try
        {
            post(() => SetStorageDisplayText(nameof(StorageMaintenanceStatus), () => Localization.Get("storage.cleaning")));
            var result = await client.SendAsync("desktop_storage_collect_garbage", null, CancellationToken.None);
            var reclaimed = ReadStatsNumber(result, "reclaimedBytes");
            post(() => SetStorageDisplayText(nameof(StorageMaintenanceStatus), () => reclaimed > 0 ? Localization.Format("storage.freed", ("size", FormatBytes(reclaimed))) : Localization.Get("storage.noneReclaimed")));
            await RefreshStorageSummaryAsync();
        }
        catch (Exception error)
        {
            var reasonKey = FailureText.Key(error, "storage cleanup failed");
            post(() => SetStorageDisplayText(nameof(StorageMaintenanceStatus), () => Localization.Format("storage.cleanupFailed", ("reason", Localization.Get(reasonKey)))));
        }
    }

    /// <summary>Full reset (state store + playback). The UI must confirm first.</summary>
    public async Task<bool> ResetAllDataAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Storage.ResetAllDataAsync");
        if (client is null || !CanReset)
        {
            return false;
        }

        try
        {
            post(() => SetStorageDisplayText(nameof(StorageMaintenanceStatus), () => Localization.Get("storage.resetting")));
            await client.SendAsync("desktop_state_reset_all_data", null, CancellationToken.None);
            CloseRemote();
            await LoadStructureAsync();
            await LoadRemoteConnectionsAsync();
            await RefreshStorageSummaryAsync();
            post(() =>
            {
                SetStorageDisplayText(nameof(StorageMaintenanceStatus), () => Localization.Get("storage.resetDone"));
                SetStorageDisplayText(nameof(ListeningStatsText), () => string.Empty);
            });
            return true;
        }
        catch (Exception error)
        {
            var reasonKey = FailureText.Key(error, "data reset failed");
            post(() => SetStorageDisplayText(nameof(StorageMaintenanceStatus), () => Localization.Format("storage.resetFailed", ("reason", Localization.Get(reasonKey)))));
            return false;
        }
    }

    private static int ReadStatsInt(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
    private static double ReadStatsNumber(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
    private static string ReadStatsText(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
    // ---- M1b：曲库自动扫描（watch 监听 + 启动扫描）与诊断日志 ----
    /// <summary>旧版防抖（storageWatchController：STORAGE_WATCH_DEBOUNCE_MS）。</summary>
    private const int StorageWatchDebounceMs = 1200;
    /// <summary>旧版启动自动扫描延迟（ofplayerApp：STARTUP_AUTO_SCAN_DELAY_MS）。</summary>
    private const int StartupScanDelayMs = 900;
    /// <summary>旧版默认诊断端点（diagnosticsReportService.ts）。</summary>
    private const string DiagnosticsReportEndpoint = "https://api.ofplayer.org/v1/diagnostics/reports";
    private bool autoScanOnLaunch;
    private IReadOnlyList<string> libraryScanDirectories = Array.Empty<string>();
    private bool isManualLibraryScanRunning;
    private string manualLibraryScanStatusText = string.Empty;
    private string diagnosticsStatusText = string.Empty;
    private string diagnosticsUploadStatusText = string.Empty;
    private int storageWatchGeneration;
    private int startupScanGeneration;
    public bool AutoScanOnLaunch => autoScanOnLaunch;
    /// <summary>M1b: library scan directories watched for auto-import (preferences-backed).</summary>
    public IReadOnlyList<string> LibraryScanDirectories => libraryScanDirectories;
    public bool CanRunManualLibraryScan => IsConnected && !IsRemoteMode && !IsImporting && Volatile.Read(ref importOperationGate) == 0 && !IsManualLibraryScanRunning && !string.IsNullOrEmpty(ActiveLibraryId) && libraryScanDirectories.Any(path => !string.IsNullOrWhiteSpace(path));
    public bool IsManualLibraryScanRunning { get => isManualLibraryScanRunning; private set => SetProperty(ref isManualLibraryScanRunning, value); }
    public string ManualLibraryScanStatusText { get => manualLibraryScanStatusText; private set => SetProperty(ref manualLibraryScanStatusText, value); }

    /// <summary>Interactive scan of the configured folders into the library selected at click time.</summary>
    public async Task RunManualLibraryScanAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Storage.RunManualLibraryScanAsync");
        if (!CanRunManualLibraryScan)
            return;
        var libraryId = ActiveLibraryId;
        var directories = libraryScanDirectories.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        if (directories.Length == 0)
            return;
        IsManualLibraryScanRunning = true;
        SetStorageDisplayText(nameof(ManualLibraryScanStatusText), () => Localization.Get("scan.running"));
        try
        {
            var outcome = await RunImportAsync("desktop_library_scan_import", new { libraryId, directories, // The explicit button mirrors Tauri's interactive scan. It does
 // not change the auto-scan preference or the automatic scan policy.
            respectDeletedImportPaths = false, }, Localization.Get("scan.label"), targetLibraryId: libraryId);
            var outcomeKey = outcome switch
            {
                ImportOutcome.Succeeded when ActiveLibraryId != libraryId => "scan.doneOriginalLibrary",
                ImportOutcome.Succeeded => "scan.done",
                ImportOutcome.Cancelled => "scan.cancelled",
                ImportOutcome.Failed => "scan.failed",
                _ => "scan.notStarted",
            };
            SetStorageDisplayText(nameof(ManualLibraryScanStatusText), () => Localization.Get(outcomeKey));
        }
        catch (Exception error)
        {
            SetStorageDisplayText(nameof(ManualLibraryScanStatusText), () => Localization.Get("scan.incomplete"));
            ReportCommandError(error);
        }
        finally
        {
            IsManualLibraryScanRunning = false;
        }
    }

    public string DiagnosticsStatusText { get => diagnosticsStatusText; private set => SetProperty(ref diagnosticsStatusText, value); }
    public string DiagnosticsUploadStatusText { get => diagnosticsUploadStatusText; private set => SetProperty(ref diagnosticsUploadStatusText, value); }

    public async Task SetAutoScanOnLaunchAsync(bool enabled)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Storage.SetAutoScanOnLaunchAsync");
        if (client is null || autoScanOnLaunch == enabled)
        {
            return;
        }

        post(() =>
        {
            autoScanOnLaunch = enabled;
            RaisePropertyChanged(nameof(AutoScanOnLaunch));
        });
        await PersistPreferencesAsync(new { autoScanOnLaunch = enabled });
        SyncStorageWatchAsync();
    }

    public async Task AddLibraryScanDirectoryAsync(string directory)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Storage.AddLibraryScanDirectoryAsync");
        if (client is null || string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var next = libraryScanDirectories.ToList();
        if (!next.Contains(directory))
        {
            next.Add(directory);
        }

        await SaveLibraryScanDirectoriesAsync(next);
    }

    public async Task RemoveLibraryScanDirectoryAsync(string directory)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Storage.RemoveLibraryScanDirectoryAsync");
        if (client is null)
        {
            return;
        }

        var next = libraryScanDirectories.Where(entry => entry != directory).ToList();
        await SaveLibraryScanDirectoriesAsync(next);
    }

    private async Task SaveLibraryScanDirectoriesAsync(IReadOnlyList<string> directories)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Storage.SaveLibraryScanDirectoriesAsync");
        if (client is null)
        {
            return;
        }

        try
        {
            await client.SendAsync("desktop_preferences_save", new { patch = new { scanDirectories = directories } }, CancellationToken.None);
            post(() =>
            {
                libraryScanDirectories = directories.ToArray();
                RaisePropertyChanged(nameof(LibraryScanDirectories));
            });
            SyncStorageWatchAsync();
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    /// <summary>旧版 syncWatch：enabled = autoScanOnLaunch & & scanDirectories.Length > 0。</summary>
    public void SyncStorageWatchAsync() => post(() => _ = SyncStorageWatchCoreAsync());
    private async Task SyncStorageWatchCoreAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Storage.SyncStorageWatchCoreAsync");
        var activeClient = client;
        if (activeClient is null)
        {
            return;
        }

        var directories = libraryScanDirectories.ToArray();
        try
        {
            await activeClient.SendAsync("storage_configure_watch", new { request = new { storageRoot = hostService?.DataRoot ?? string.Empty, directories, enabled = autoScanOnLaunch && directories.Length > 0, }, }, CancellationToken.None);
        }
        catch (Exception error)
        {
            OFPlayer_WinUI.Services.PreviewLog.Write($"storage watch configure failed: {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>旧版存储事件入口：error 与空路径忽略，其余进入 1200ms 防抖。</summary>
    private void HandleStorageWatchEvent(JsonElement data)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Storage.HandleStorageWatchEvent");
        var kind = data.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String ? kindElement.GetString() : null;
        if (string.Equals(kind, "error", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var hasPaths = data.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array && paths.GetArrayLength() > 0;
        if (!hasPaths)
        {
            return;
        }

        var generation = ++storageWatchGeneration;
        _ = DebouncedStorageWatchScanAsync(generation);
    }

    private async Task DebouncedStorageWatchScanAsync(int generation)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Storage.DebouncedStorageWatchScanAsync");
        await Task.Delay(StorageWatchDebounceMs);
        if (generation != storageWatchGeneration || closing || !autoScanOnLaunch)
        {
            return;
        }

        await RunAutomaticScanAsync("scan.autoChanged");
    }

    /// <summary>旧版启动自动扫描：延迟 900ms 后（条件满足时）扫描一次。</summary>
    private void ScheduleStartupScan()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Storage.ScheduleStartupScan");
        var generation = ++startupScanGeneration;
        _ = DelayedStartupScanAsync(generation);
    }

    private async Task DelayedStartupScanAsync(int generation)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Storage.DelayedStartupScanAsync");
        await Task.Delay(StartupScanDelayMs);
        if (generation != startupScanGeneration || closing || !autoScanOnLaunch)
        {
            return;
        }

        await RunAutomaticScanAsync("scan.autoStartup");
    }

    private async Task RunAutomaticScanAsync(string statusLabelKey)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Storage.RunAutomaticScanAsync");
        var directories = libraryScanDirectories.ToArray();
        if (directories.Length == 0 || client is null || IsImporting || string.IsNullOrEmpty(ActiveLibraryId))
        {
            // 导入进行中：本次跳过（与旧版 runLibraryScanImport 的忙守卫一致）。
            return;
        }

        post(() => ImportStatusText = Localization.Get(statusLabelKey));
        await RunImportAsync("desktop_library_scan_import", new { libraryId = ActiveLibraryId, directories, // 非交互扫描保留删除记录（旧版 respectDeletedImportPaths 默认 true）。
 respectDeletedImportPaths = true, }, Localization.Get("scan.label"));
    }

    public async Task RefreshDiagnosticsStatusAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Storage.RefreshDiagnosticsStatusAsync");
        if (client is null)
        {
            return;
        }

        try
        {
            var result = await client.SendAsync("diagnostics_log_status", null, CancellationToken.None);
            var path = ReadStatsText(result, "path");
            var directory = ReadStatsText(result, "directory");
            var kind = ReadStatsText(result, "directoryKind");
            post(() => SetStorageDisplayText(nameof(DiagnosticsStatusText), () => string.IsNullOrEmpty(path) ? Localization.Get("diagnostics.notReady") : Localization.Format("diagnostics.location", ("path", path), ("directory", directory), ("kind", kind))));
        }
        catch (Exception error)
        {
            var reasonKey = FailureText.Key(error, "diagnostics status failed");
            post(() => SetStorageDisplayText(nameof(DiagnosticsStatusText), () => Localization.Format("diagnostics.statusFailed", ("reason", Localization.Get(reasonKey)))));
        }
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(field, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var items = new List<string>();
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var text = entry.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    items.Add(text);
                }
            }
        }

        return items;
    }
}
