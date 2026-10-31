using System.Collections.ObjectModel;
using System.Text.Json;
using OFPlayer.WinUI.Core.Lyrics;
using OFPlayer_WinUI.Models;
using OFPlayer_WinUI.Services;

namespace OFPlayer_WinUI.ViewModels;
/// <summary>
/// N3 lyrics panel: resolve / bind / clear / reparse for the active track plus
/// a synchronized line list. Rendering stays native in the main window; the
/// animated Vue renderer lives in the WebView2 immersive window (per the
/// migration plan: avoid interleaving native controls with WebView surfaces).
///
/// R4-3: rows are now grouped display lines produced by <see cref = "LyricTimeline"/>
/// (same rule as the old front-end `createLyricPlayerLines`), so bilingual LRC
/// files no longer render as duplicated rows, and romanization/end-time/offset
/// survive the trip from the host instead of being dropped.
/// </summary>
public sealed partial class MainViewModel
{
    private bool isLyricsVisible;
    private string lyricsStatusText = string.Empty;
    private string lyricsSourceText = string.Empty;
    private string lyricsMetaText = string.Empty;
    private string? lyricsTrackId;
    private int lyricsRequestGeneration;
    private readonly HashSet<int> activeLyricIndices = new();
    private readonly List<LyricTimeline.DisplayLine> lyricTimeline = new();
    /// <summary>Bumped on every mutation of the lyric data; lets Web surfaces detect
    /// same-track edits (bind/clear/reparse) which a trackId check cannot see.</summary>
    private int lyricsVersion;
    public ObservableCollection<LyricLineRow> LyricsLines { get; } = new();

    /// <summary>Raised on the UI thread when line emphasis changes; carries the primary active line.</summary>
    public event Action<LyricLineRow?>? ActiveLyricsChanged;
    public bool IsLyricsVisible
    {
        get => isLyricsVisible;
        private set
        {
            SetProperty(ref isLyricsVisible, value);
        }
    }

    public string LyricsStatusText { get => lyricsStatusText; private set => SetProperty(ref lyricsStatusText, value); }
    public string LyricsSourceText { get => lyricsSourceText; private set => SetProperty(ref lyricsSourceText, value); }
    /// <summary>LRC tags (ti/ar/al/by/offset) summarized for the panel header.</summary>
    public string LyricsMetaText { get => lyricsMetaText; private set => SetProperty(ref lyricsMetaText, value); }
    public bool HasLyricsLines => LyricsLines.Count > 0;
    /// <summary>
    /// Monotonic version of the current lyric payload. Web surfaces compare this to
    /// detect a same-track edit; the pair (trackId, lyricsVersion) is the broadcast key.
    /// </summary>
    public int LyricsVersion => lyricsVersion;
    /// <summary>Track the current lyric payload belongs to (null when none).</summary>
    public string? ActiveLyricsTrackId => lyricsTrackId;

    /// <summary>Raised after the active track's lyrics change, including same-track edits.</summary>
    public event Action? LyricsChanged;
    public async Task ToggleLyricsAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Lyrics.ToggleLyricsAsync");
        if (client is null)
        {
            return;
        }

        IsLyricsVisible = !IsLyricsVisible;
        if (IsLyricsVisible)
        {
            await ReloadLyricsAsync(playback.PositionSeconds);
        }
    }

    /// <summary>Resolves lyrics for the active track (bind-file / sibling / embedded / scan dirs).</summary>
    public async Task ReloadLyricsAsync(double positionSeconds)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Lyrics.ReloadLyricsAsync");
        var activeClient = client;
        var trackId = playback.ActiveTrackId;
        var generation = ++lyricsRequestGeneration;
        var sourceGeneration = remotePlayGeneration;
        var togetherScope = PlaybackPresentationScope;
        bool Current() => generation == lyricsRequestGeneration && activeClient == client && trackId == playback.ActiveTrackId && sourceGeneration == remotePlayGeneration && togetherScope == PlaybackPresentationScope;
        if (activeClient is null)
        {
            return;
        }

        if (trackId is null)
        {
            ClearLyricsView(Localization.Get("lyrics.nothingPlaying"), Current);
            return;
        }

        try
        {
            var result = await ResolvePlaybackLyricsAsync(trackId, positionSeconds);
            if (!Current())
                return;
            if (result.ValueKind != JsonValueKind.Object)
            {
                ClearLyricsView(Localization.Get("lyrics.none"), Current);
                return;
            }

            var status = ReadString(result, "status") ?? "unknown";
            var kind = ReadString(result, "kind") ?? string.Empty;
            var sourcePath = ReadString(result, "sourcePath") ?? string.Empty;
            var meta = BuildLyricsMeta(result);
            var rawLines = result.TryGetProperty("lines", out var lines) ? LyricHostPayload.ParseLines(lines) : Array.Empty<LyricTimeline.InputLine>();
            // Group before marshalling: bilingual entries collapse into one row here,
            // so the UI thread only ever receives display-ready rows.
            var timeline = LyricTimeline.CreateDisplayLines(rawLines, disableBilingualGrouping: ReadString(result, "source") == "sidecar-ttml");
            var rows = new List<LyricLineRow>(timeline.Count);
            foreach (var line in timeline)
            {
                rows.Add(new LyricLineRow(line.Index, line.Text, line.Translation, false) { Roman = line.Roman, Words = line.Words, IsBackground = line.IsBackground, IsDuet = line.IsDuet, Agent = line.Agent, StartSeconds = line.StartSeconds, EndSeconds = line.EndSeconds, });
            }

            post(() =>
            {
                // Drop a late response that lost its track to a newer request.
                if (!Current())
                {
                    return;
                }

                lyricsTrackId = trackId;
                activeLyricIndices.Clear();
                lyricTimeline.Clear();
                lyricTimeline.AddRange(timeline);
                LyricsLines.Clear();
                foreach (var row in rows)
                {
                    LyricsLines.Add(row);
                }

                var unsynced = kind == "unsynced";
                LyricsStatusText = ReadString(result, "reason") == "together-lyrics-unavailable" ? Localization.Get("lyrics.togetherUnavailable") : rows.Count == 0 ? Localization.Get("lyrics.none") : (unsynced ? Localization.Get("lyrics.unsynced") : string.Empty);
                LyricsSourceText = sourcePath;
                LyricsMetaText = meta;
                RaisePropertyChanged(nameof(HasLyricsLines));
                UpdateActiveLyricLine(positionSeconds);
                // Same-track edits must broadcast: bump the version and notify every
                // surface (the Web renderer included) without waiting for a track change.
                lyricsVersion++;
                RaisePropertyChanged(nameof(LyricsVersion));
                LyricsChanged?.Invoke();
            });
        }
        catch (Exception error)
        {
            if (Current())
            {
                var reason = Services.FailureText.Reason(error, "lyrics load failed");
                ClearLyricsView(Localization.Format("lyrics.failed", ("reason", reason)), Current);
            }
        }
    }

    /// <summary>LRC header tags surfaced in the panel (old snapshot metadata).</summary>
    private static string BuildLyricsMeta(JsonElement result)
    {
        var parts = new List<string>();
        void Append(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add($"{label} {value.Trim()}");
            }
        }

        Append(Localization.Get("lyrics.meta.title"), ReadString(result, "title"));
        Append(Localization.Get("lyrics.meta.artist"), ReadString(result, "artist"));
        Append(Localization.Get("lyrics.meta.album"), ReadString(result, "album"));
        Append(Localization.Get("lyrics.meta.by"), ReadString(result, "by"));
        Append(Localization.Get("lyrics.meta.language"), ReadString(result, "language"));
        var offset = ReadNumber(result, "offsetMs");
        if (offset is { } value2 && Math.Abs(value2) >= 1)
        {
            // The old client parsed but never applied offsetMs; surface it so the
            // number is visible rather than silently ignored.
            parts.Add(Localization.Format("lyrics.meta.offset", ("ms", value2.ToString("0", System.Globalization.CultureInfo.InvariantCulture))));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Bind an .ttml/.lrc/.txt file to the active track.</summary>
    public async Task BindLyricsAsync(string path)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Lyrics.BindLyricsAsync");
        if (client is null || string.IsNullOrWhiteSpace(path) || playback.ActiveTrackId is null)
        {
            return;
        }

        try
        {
            await client.SendAsync("desktop_catalog_update_track", new { trackId = playback.ActiveTrackId, patch = new { lyricsPath = path } }, CancellationToken.None);
            await ReloadLyricsAsync(playback.PositionSeconds);
            post(() =>
            {
                StatusKind = StatusKind.Success;
                StatusMessage = Localization.Get("lyrics.bound");
            });
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    /// <summary>Clear the bound file (old front-end `clearLyricsBinding`).</summary>
    public async Task ClearLyricsAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Lyrics.ClearLyricsAsync");
        if (client is null || playback.ActiveTrackId is null)
        {
            return;
        }

        try
        {
            await client.SendAsync("desktop_catalog_update_track", new { trackId = playback.ActiveTrackId, patch = new { lyricsPath = string.Empty } }, CancellationToken.None);
            await ReloadLyricsAsync(playback.PositionSeconds);
            post(() =>
            {
                StatusKind = StatusKind.Success;
                StatusMessage = Localization.Get("lyrics.cleared");
            });
        }
        catch (Exception error)
        {
            ReportCommandError(error);
        }
    }

    public Task ReparseLyricsAsync() => ReloadLyricsAsync(playback.PositionSeconds);
    /// <summary>Click a lyric line to seek (same as the immersive page).</summary>
    public async Task SeekToLyricLineAsync(LyricLineRow row)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Lyrics.SeekToLyricLineAsync");
        // Look the row up by identity in the *display* list: after grouping, a row's
        // Index is the raw LRC index and no longer addresses the display collection.
        var slot = LyricsLines.IndexOf(row);
        if (slot < 0 || slot >= lyricTimeline.Count)
        {
            return;
        }

        var start = lyricTimeline[slot].StartSeconds;
        if (start is not null)
        {
            await SeekAsync(start.Value);
        }
    }

    /// <summary>Called from playback snapshots; updates the highlight without rebuilding rows.</summary>
    private void UpdateActiveLyricLine(double positionSeconds)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Lyrics.UpdateActiveLyricLine");
        if (LyricsLines.Count == 0 || !IsLyricsVisible)
        {
            return;
        }

        var primary = LyricTimeline.FindActiveIndex(lyricTimeline, positionSeconds);
        var next = new HashSet<int>();
        if (primary >= 0)
            next.Add(primary);
        if (lyricTimeline.Any(line => line.IsBackground || line.IsDuet))
        {
            var positionMs = LyricTimeline.ToMs(positionSeconds);
            for (var index = 0; index < lyricTimeline.Count; index++)
            {
                var line = lyricTimeline[index];
                if ((line.IsBackground || line.IsDuet) && line.StartMs <= positionMs && positionMs < line.EndMs)
                    next.Add(index);
            }
        }

        var changed = false;
        foreach (var index in activeLyricIndices.Union(next).ToArray())
        {
            var shouldBeActive = next.Contains(index);
            if (index >= 0 && index < LyricsLines.Count && LyricsLines[index].IsActive != shouldBeActive)
            {
                LyricsLines[index].IsActive = shouldBeActive;
                changed = true;
            }
        }

        activeLyricIndices.Clear();
        activeLyricIndices.UnionWith(next);
        if (changed)
            ActiveLyricsChanged?.Invoke(primary >= 0 && primary < LyricsLines.Count ? LyricsLines[primary] : null);
    }

    private void ClearLyricsView(string status, Func<bool>? isCurrent = null)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Lyrics.ClearLyricsView");
        post(() =>
        {
            if (isCurrent?.Invoke() == false)
                return;
            lyricsTrackId = null;
            activeLyricIndices.Clear();
            lyricTimeline.Clear();
            LyricsLines.Clear();
            LyricsStatusText = status;
            LyricsSourceText = string.Empty;
            LyricsMetaText = string.Empty;
            RaisePropertyChanged(nameof(HasLyricsLines));
            // Clearing is also a same-track change: bind/clear must reach Web surfaces.
            lyricsVersion++;
            RaisePropertyChanged(nameof(LyricsVersion));
            LyricsChanged?.Invoke();
        });
    }
}
