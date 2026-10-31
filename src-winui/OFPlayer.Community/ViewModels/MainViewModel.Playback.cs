using System.Text.Json;
using OFPlayer.WinUI.Core.Playback;
using OFPlayer_WinUI.Models;
using OFPlayer_WinUI.Services;

namespace OFPlayer_WinUI.ViewModels;
/// <summary>
/// N2 playback orchestration: repeat/shuffle order (ported from the old
/// front-end `playbackOrder.ts`), queue-correct selection, current-track detail
/// (cover + technical metadata), volume memory and output-device management.
///
/// The Rust host stays the single playback engine; this partial only decides
/// WHICH track to select (old front-end semantics: next/previous/ended are
/// resolved client-side, then `playback_session_select_track` executes).
/// </summary>
public sealed partial class MainViewModel
{
    private const double DefaultVolume = 0.8;
    private string repeatMode = PlaybackOrder.RepeatAll;
    private bool shuffleEnabled;
    private double rememberedVolume = DefaultVolume;
    private bool rememberVolume = true;
    private bool preferencesLoaded;
    private bool telemetryEnabled = true;
    private string themePreference = "system";
    private string languagePreference = DefaultLanguage;
    private string immersiveTaskbarModePreference = DefaultImmersiveTaskbarMode;
    private string closeBehaviorPreference = DefaultCloseBehavior;
    private string storageSummaryText = string.Empty;
    private List<string> shuffledOrder = new();
    private CancellationTokenSource? volumeSaveDebounce;
    private TrackDetail? currentTrackDetail;
    private readonly Dictionary<string, TrackDetail> trackDetailCache = new();
    private PlaybackOutputDevicesSnapshot outputDevices = PlaybackOutputDevicesSnapshot.Empty;
    private bool isDetailsOpen = true;
    public string RepeatMode => repeatMode;
    public bool ShuffleEnabled => shuffleEnabled;
    public TrackDetail? CurrentTrackDetail { get => currentTrackDetail?.Id == playback.ActiveTrackId ? currentTrackDetail : null; private set => SetProperty(ref currentTrackDetail, value); }
    public IReadOnlyList<PlaybackOutputDevice> OutputDevices => outputDevices.Devices;
    public string? ActiveOutputDeviceId => outputDevices.ActiveDeviceId;
    public bool IsDetailsOpen { get => isDetailsOpen; set => SetProperty(ref isDetailsOpen, value); }
    public double PreferredVolume => rememberedVolume;
    public bool RememberVolume => rememberVolume;
    public bool TelemetryEnabled => telemetryEnabled;
    public string ThemePreference => themePreference;
    /// <summary>R4-2: `system` | `en` | `zh-CN`, mirroring the old LanguageOption.</summary>
    public string LanguagePreference => languagePreference;
    /// <summary>R4-2: `show` | `hide` — immersive taskbar mode, shared with the overlay button.</summary>
    public string ImmersiveTaskbarModePreference => immersiveTaskbarModePreference;
    /// <summary>R4-2: the taskbar is hidden only when the persisted mode says so.</summary>
    public bool ImmersiveTaskbarHidden => immersiveTaskbarModePreference == "hide";
    /// <summary>`ask` | `minimize` | `exit` — what closing the window does. Ask is the default.</summary>
    public string CloseBehaviorPreference => closeBehaviorPreference;
    public string StorageSummaryText { get => storageSummaryText; private set => SetProperty(ref storageSummaryText, value); }
    public string PlayModeLabel => Localization.Get(ShuffleEnabled ? "player.mode.shuffle" : RepeatMode switch
    {
        PlaybackOrder.RepeatOne => "player.mode.repeatOne",
        PlaybackOrder.RepeatNone => "player.mode.inOrder",
        _ => "player.mode.repeatAll",
    });
    /// <summary>No specific device chosen (or known): output follows the Windows default.</summary>
    public bool OutputUsesSystemDefault => outputDevices.PrefersSystemDefault || string.IsNullOrEmpty(outputDevices.ActiveDeviceName);
    public string OutputDeviceLabel => OutputUsesSystemDefault ? Localization.Get("player.output.system") : outputDevices.ActiveDeviceName!;

    // ---- 播放模式（旧版 cyclePlaybackMode：顺序→列表循环→单曲循环→随机→顺序）----
    public void CyclePlayMode()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.CyclePlayMode");
        (repeatMode, shuffleEnabled) = PlaybackModeCycle.Next(repeatMode, shuffleEnabled);
        shuffledOrder = shuffleEnabled ? PlaybackOrder.CreateShuffled(currentQueueTrackIds, playback.ActiveTrackId) : new List<string>();
        RaisePropertyChanged(nameof(RepeatMode));
        RaisePropertyChanged(nameof(ShuffleEnabled));
        RaisePropertyChanged(nameof(PlayModeLabel));
        _ = PersistPreferencesAsync(new { repeatMode, shuffleEnabled });
    }

    /// <summary>Queue order honoring shuffle (rebuilds when the queue changed).</summary>
    private List<string> GetPlaybackQueueOrder()
    {
        var queue = PlaybackOrder.Normalize(currentQueueTrackIds);
        if (!shuffleEnabled)
        {
            return queue;
        }

        var queueSet = queue.ToHashSet(StringComparer.Ordinal);
        var filtered = shuffledOrder.Where(queueSet.Contains).ToList();
        var complete = filtered.Count == queue.Count && filtered.Distinct(StringComparer.Ordinal).Count() == queue.Count;
        if (complete)
        {
            return filtered;
        }

        shuffledOrder = shuffledOrder.Count == 0 ? PlaybackOrder.CreateShuffled(queue, playback.ActiveTrackId) : QueueObservationOrder.ReconcileShuffle(shuffledOrder, queue);
        return shuffledOrder;
    }

    private string? ResolveOrderTrackId(int step, string reason) => PlaybackOrder.Resolve(GetPlaybackQueueOrder(), playback.ActiveTrackId, shuffleEnabled ? PlaybackOrder.RepeatNone : repeatMode, step, reason);
    /// <summary>Resolves and selects the next/previous queue track (front-end order).</summary>
    private async Task PlaySkipAsync(int step, string reason)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.PlaySkipAsync");
        await queueEditCompletion;
        if (client is null)
        {
            return;
        }

        var nextTrackId = ResolveOrderTrackId(step, reason);
        if (nextTrackId is null)
        {
            if (reason == PlaybackOrder.ReasonEnded)
            {
                await RunPlaybackCommandAsync("playback_session_pause", new { });
            }

            return;
        }

        try
        {
            var result = await client.SendAsync("playback_session_select_track", new { trackId = nextTrackId, autoplay = true }, CancellationToken.None);
            ApplyCommandResult(result);
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    /// <summary>Old front-end previous: restart the track when past 3s.</summary>
    private async Task PlayPreviousInternalAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.PlayPreviousInternalAsync");
        if (playback.PositionSeconds > 3 && playback.ActiveTrackId is not null)
        {
            await SeekAsync(0);
            return;
        }

        await PlaySkipAsync(-1, PlaybackOrder.ReasonUser);
    }

    public string PlaybackTitle => CurrentTrackDetail?.Title ?? FindLoadedTrack(playback.ActiveTrackId)?.Title ?? (playback.ActiveTrackId == "external" && externalPlaybackLabel.Length > 0 ? externalPlaybackLabel : Localization.Get(IsConnected ? "player.idle" : "player.notReady"));
    public string PlaybackArtist => CurrentTrackDetail?.Artist ?? FindLoadedTrack(playback.ActiveTrackId)?.Artist ?? string.Empty;
    public string? PlaybackArtwork => CurrentTrackDetail?.Artwork;

    private string BuildPlaybackLabel()
    {
        var detail = currentTrackDetail;
        if (detail is not null && detail.Id == playback.ActiveTrackId)
        {
            var artistPart = string.IsNullOrEmpty(detail.Artist) ? string.Empty : $" · {detail.Artist}";
            return $"{detail.Title}{artistPart} · {TimeSpan.FromSeconds(playback.PositionSeconds):m\\:ss}";
        }

        var row = FindLoadedTrack(playback.ActiveTrackId);
        if (row is null)
        {
            // D4: external playback keeps the session untouched; the bar shows the
            // remembered remote-track label instead of the generic placeholder.
            if (!string.IsNullOrEmpty(externalPlaybackLabel) && (string.Equals(playback.ActiveTrackId, "external", StringComparison.Ordinal)))
            {
                return $"{externalPlaybackLabel} · {TimeSpan.FromSeconds(playback.PositionSeconds):m\\:ss}";
            }

            return Localization.Get(IsConnected ? "player.idle" : "player.notReady");
        }

        return $"{row.Title} · {TimeSpan.FromSeconds(playback.PositionSeconds):m\\:ss}";
    }

    /// <summary>Loads (and caches) the active track's detail, including artwork.</summary>
    public async Task LoadCurrentTrackDetailAsync(string trackId)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.LoadCurrentTrackDetailAsync");
        var activeClient = client;
        if (activeClient is null || string.IsNullOrEmpty(trackId))
        {
            return;
        }

        if (trackDetailCache.TryGetValue(trackId, out var cached))
        {
            post(() =>
            {
                if (playback.ActiveTrackId == trackId)
                {
                    CurrentTrackDetail = cached;
                }
            });
            return;
        }

        try
        {
            var result = await activeClient.SendAsync("desktop_catalog_get_track", new { trackId, includeArtwork = true }, CancellationToken.None);
            if (result.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var detail = TrackDetail.FromJson(result);
            trackDetailCache[trackId] = detail;
            if (trackDetailCache.Count > 8)
            {
                trackDetailCache.Remove(trackDetailCache.Keys.First());
            }

            post(() =>
            {
                if (playback.ActiveTrackId == trackId)
                {
                    CurrentTrackDetail = detail;
                }
            });
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    // ---- 音量记忆 ----
    /// <summary>Engine volume plus a debounced preferences write (rememberVolume).</summary>
    public async Task SetVolumePersistedAsync(double volume)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.SetVolumePersistedAsync");
        await RunPlaybackCommandAsync("playback_set_volume", new { volume });
        volumeSaveDebounce?.Cancel();
        volumeSaveDebounce?.Dispose();
        volumeSaveDebounce = new CancellationTokenSource();
        var token = volumeSaveDebounce.Token;
        _ = SaveVolumeAfterDelayAsync(token, volume);
    }

    private async Task SaveVolumeAfterDelayAsync(CancellationToken token, double volume)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.SaveVolumeAfterDelayAsync");
        try
        {
            await Task.Delay(800, token);
            if (rememberVolume)
            {
                rememberedVolume = Math.Clamp(volume, 0.0, 1.0);
                await PersistPreferencesAsync(new { volume = rememberedVolume });
                post(() => RaisePropertyChanged(nameof(PreferredVolume)));
            }
        }
        catch (OperationCanceledException)
        {
        // A newer volume change supersedes this write.
        }
    }

    // ---- 偏好（repeat/shuffle/volume；N3 设置页复用）----
    public async Task LoadPreferencesAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.LoadPreferencesAsync");
        if (client is null || preferencesLoaded)
        {
            return;
        }

        try
        {
            var result = await client.SendAsync("desktop_preferences_load", null, CancellationToken.None);
            preferencesLoaded = true;
            var preferences = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("preferences", out var element) && element.ValueKind == JsonValueKind.Object ? element : default;
            post(() =>
            {
                repeatMode = NormalizeRepeatMode(ReadString(preferences, "repeatMode"));
                shuffleEnabled = ReadBool(preferences, "shuffleEnabled", false);
                rememberVolume = ReadBool(preferences, "rememberVolume", true);
                rememberedVolume = Math.Clamp(ReadDouble(preferences, "volume", DefaultVolume), 0.0, 1.0);
                telemetryEnabled = ReadBool(preferences, "telemetryEnabled", true);
                themePreference = NormalizeTheme(ReadString(preferences, "theme"));
                // R4-2: the old model accepts legacy aliases for these two; read them the
                // same way so a profile written by the old client restores identically.
                languagePreference = NormalizeLanguage(ReadString(preferences, "language") ?? ReadString(preferences, "localePreference") ?? ReadString(preferences, "locale"));
                immersiveTaskbarModePreference = NormalizeImmersiveTaskbarMode(ReadString(preferences, "immersiveTaskbarMode") ?? ReadString(preferences, "immersiveFullscreenMode"));
                closeBehaviorPreference = NormalizeCloseBehavior(ReadString(preferences, "closeBehavior"));
                smartPlaylistsPreference = NormalizeSmartPlaylists(ReadString(preferences, "smartPlaylists"));
                // M1b: storage-watch preferences (old scanDirectories / autoScanOnLaunch).
                autoScanOnLaunch = ReadBool(preferences, "autoScanOnLaunch", false);
                NewTracksFirst = ReadBool(preferences, "newTracksFirst", false);
                ArtistSortOption = ReadString(preferences, "artistSortOption") == "track-count" ? "track-count" : "name";
                RaisePropertyChanged(nameof(NewTracksFirst));
                RaisePropertyChanged(nameof(ArtistSortOption));
                libraryScanDirectories = ReadStringList(preferences, "scanDirectories");
                LoadCapsuleAppearance(preferences);
                LoadContentBackdrop(preferences);
                RaisePropertyChanged(nameof(RepeatMode));
                RaisePropertyChanged(nameof(ShuffleEnabled));
                RaisePropertyChanged(nameof(PlayModeLabel));
                RaisePropertyChanged(nameof(PreferredVolume));
                RaisePropertyChanged(nameof(RememberVolume));
                RaisePropertyChanged(nameof(TelemetryEnabled));
                RaisePropertyChanged(nameof(ThemePreference));
                RaisePropertyChanged(nameof(LanguagePreference));
                RaisePropertyChanged(nameof(ImmersiveTaskbarModePreference));
                RaisePropertyChanged(nameof(ImmersiveTaskbarHidden));
                RaisePropertyChanged(nameof(CloseBehaviorPreference));
                RaisePropertyChanged(nameof(SmartPlaylistsPreference));
                RaisePropertyChanged(nameof(AutoScanOnLaunch));
                RaisePropertyChanged(nameof(LibraryScanDirectories));
                // D3: restore the persisted sort option (queried with defaults already).
                collectionSorting.Load(preferences);
                var savedSort = collectionSorting.Resolve(ActiveCollectionRef);
                if (!string.IsNullOrEmpty(savedSort) && savedSort != sortOption)
                {
                    sortOption = savedSort;
                    RaisePropertyChanged(nameof(SortOption));
                    _ = LoadFirstPageAsync();
                }
            });
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    private async Task PersistPreferencesAsync(object patch)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.PersistPreferencesAsync");
        if (client is null)
        {
            return;
        }

        try
        {
            await client.SendAsync("desktop_preferences_save", new { patch }, CancellationToken.None);
        }
        catch (Exception error)
        {
            OFPlayer_WinUI.Services.PreviewLog.Write($"preferences save failed: {error.GetType().Name}: {error.Message}");
        }
    }

    private static string NormalizeRepeatMode(string? value) => value switch
    {
        PlaybackOrder.RepeatNone => PlaybackOrder.RepeatNone,
        PlaybackOrder.RepeatOne => PlaybackOrder.RepeatOne,
        _ => PlaybackOrder.RepeatAll,
    };
    private static string NormalizeTheme(string? value) => value is "light" or "dark" ? value : "system";
    // ---- R4-2：设置与多语言的取值域（与旧 src/models/preferences.ts 对齐）----
    //
    // The option sets and defaults are copied verbatim from the old model so the two
    // clients read and write the same values; anything unrecognised falls back to the
    // documented default instead of being written through.
    internal const string DefaultLanguage = "system";
    internal const string DefaultImmersiveTaskbarMode = "show";
    internal const string DefaultCloseBehavior = "ask";
    internal static readonly string[] LanguageOptions =
    {
        "system",
        "en",
        "zh-CN",
        "ja",
        "fr",
        "de"
    };
    internal static readonly string[] ImmersiveTaskbarModeOptions =
    {
        "show",
        "hide"
    };
    private static string NormalizeLanguage(string? value) => value is "system" or "en" or "zh-CN" or "ja" or "fr" or "de" ? value : DefaultLanguage;
    private static string NormalizeImmersiveTaskbarMode(string? value) => value is "show" or "hide" ? value : DefaultImmersiveTaskbarMode;
    private static string NormalizeCloseBehavior(string? value) => value is "ask" or "minimize" or "exit" ? value : DefaultCloseBehavior;
    // ---- N3 设置：记忆音量 / 诊断同意 / 主题 / 存储分析 ----
    public async Task SetRememberVolumeAsync(bool value)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.SetRememberVolumeAsync");
        rememberVolume = value;
        post(() => RaisePropertyChanged(nameof(RememberVolume)));
        await PersistPreferencesAsync(new { rememberVolume = value });
    }

    public async Task SetThemeAsync(string theme)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.SetThemeAsync");
        themePreference = NormalizeTheme(theme);
        post(() => RaisePropertyChanged(nameof(ThemePreference)));
        await PersistPreferencesAsync(new { theme = themePreference });
    }

    /// <summary>
    /// R4-2: language selection. Persisted under the old client's primary key; a
    /// restart re-reads it and the localization service follows it live.
    /// </summary>
    public async Task SetLanguageAsync(string language)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.SetLanguageAsync");
        languagePreference = NormalizeLanguage(language);
        post(() => RaisePropertyChanged(nameof(LanguagePreference)));
        await PersistPreferencesAsync(new { language = languagePreference });
    }

    /// <summary>
    /// R4-2: immersive taskbar mode. Persisted so the settings selector and the
    /// in-overlay taskbar button stay in sync across entries and restarts.
    /// </summary>
    public async Task SetImmersiveTaskbarModeAsync(string mode)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.SetImmersiveTaskbarModeAsync");
        immersiveTaskbarModePreference = NormalizeImmersiveTaskbarMode(mode);
        post(() =>
        {
            RaisePropertyChanged(nameof(ImmersiveTaskbarModePreference));
            RaisePropertyChanged(nameof(ImmersiveTaskbarHidden));
        });
        await PersistPreferencesAsync(new { immersiveTaskbarMode = immersiveTaskbarModePreference });
    }

    /// <summary>
    /// What closing the window does: ask every time, minimize, or quit. Stored with the
    /// other preferences, so the close dialog's "don't ask again" and the settings
    /// selector read and write the same value.
    /// </summary>
    public async Task SetCloseBehaviorAsync(string behavior)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.SetCloseBehaviorAsync");
        closeBehaviorPreference = NormalizeCloseBehavior(behavior);
        post(() => RaisePropertyChanged(nameof(CloseBehaviorPreference)));
        await PersistPreferencesAsync(new { closeBehavior = closeBehaviorPreference });
    }

    /// <summary>Fills the settings storage section from `desktop_storage_analyze`.</summary>
    public async Task RefreshStorageSummaryAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.RefreshStorageSummaryAsync");
        if (client is null)
        {
            return;
        }

        try
        {
            var result = await client.SendAsync("desktop_storage_analyze", null, CancellationToken.None);
            var lines = new List<string>();
            if (result.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in result.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Number)
                    {
                        lines.Add($"{property.Name}: {FormatBytes(property.Value.GetDouble())}");
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var nested in property.Value.EnumerateObject())
                        {
                            if (nested.Value.ValueKind == JsonValueKind.Number)
                            {
                                lines.Add($"{nested.Name}: {FormatBytes(nested.Value.GetDouble())}");
                            }
                        }
                    }
                }
            }

            post(() => StorageSummaryText = lines.Count > 0 ? string.Join(Environment.NewLine, lines) : Services.Localization.Get("storage.noData"));
        }
        catch (Exception error)
        {
            ReportCommandError(error);
            var reasonKey = Services.FailureText.Key(error, "storage analysis failed");
            post(() => StorageSummaryText = Services.Localization.Format("storage.analysisFailed", ("reason", Services.Localization.Get(reasonKey))));
        }
    }

    private static string FormatBytes(double bytes) => bytes switch
    {
        >= 1_048_576 => (bytes / 1_048_576).ToString("0.##", Services.Localization.Culture) + " MB",
        >= 1_024 => (bytes / 1_024).ToString("0.#", Services.Localization.Culture) + " KB",
        _ => bytes.ToString("0", Services.Localization.Culture) + " B",
    };
    // ---- 输出设备 ----
    public async Task RefreshOutputDevicesAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.RefreshOutputDevicesAsync");
        if (client is null)
        {
            return;
        }

        try
        {
            var result = await client.SendAsync("playback_list_output_devices", null, CancellationToken.None);
            var snapshot = PlaybackOutputDevicesSnapshot.FromJson(result);
            post(() =>
            {
                outputDevices = snapshot;
                RaisePropertyChanged(nameof(OutputDevices));
                RaisePropertyChanged(nameof(OutputDeviceLabel));
            });
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    public async Task SetOutputDeviceAsync(string? deviceId)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.SetOutputDeviceAsync");
        if (client is null)
        {
            return;
        }

        try
        {
            var result = await client.SendAsync("playback_set_output_device", new { deviceId }, CancellationToken.None);
            ApplyCommandResult(result);
            await RefreshOutputDevicesAsync();
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    public async Task RecoverOutputAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.RecoverOutputAsync");
        if (client is null)
        {
            return;
        }

        try
        {
            var result = await client.SendAsync("playback_recover_output", null, CancellationToken.None);
            ApplyCommandResult(result);
            await RefreshOutputDevicesAsync();
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    /// <summary>
    /// Restores the volume after connecting: the remembered value when volume
    /// memory is enabled, otherwise the product default (D0 fix - the engine
    /// must not adopt a stale remembered volume while memory is off).
    /// </summary>
    public void ApplyPreferredVolumeToEngine()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Playback.ApplyPreferredVolumeToEngine");
        if (client is null)
        {
            return;
        }

        var targetVolume = rememberedVolume;
        if (!rememberVolume)
        {
            targetVolume = DefaultVolume;
            rememberedVolume = DefaultVolume;
            RaisePropertyChanged(nameof(PreferredVolume));
        }

        _ = RunPlaybackCommandAsync("playback_set_volume", new { volume = targetVolume });
    }

    private static string? ReadString(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool ReadBool(JsonElement element, string field, bool fallback) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) ? value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => fallback,
    } : fallback;
    private static double ReadDouble(JsonElement element, string field, double fallback) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;
}
