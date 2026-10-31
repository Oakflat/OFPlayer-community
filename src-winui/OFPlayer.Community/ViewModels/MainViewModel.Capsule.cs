using System.Text.Json;
using System.Text.Json.Nodes;
using OFPlayer.WinUI.Core.Lyrics;
using OFPlayer_WinUI.Services;

namespace OFPlayer_WinUI.ViewModels;
/// <summary>
/// M2 lyric capsule data: builds the capsule boot state (old `capsule_get_boot_state`
/// shape) from the live playback snapshot plus host track/lyrics queries, and the
/// lightweight progress anchor frames (old `capsule://progress-anchor`).
/// </summary>
public sealed partial class MainViewModel
{
    private long capsuleSeq;
    private long capsuleLyricVersion;
    /// <summary>
    /// Token for the in-flight capsule build. Comparing the *track id* alone is not
    /// enough to discard a stale build: with A → B → A the slow first A lookup still
    /// matches the current track, yet it reflects a position/edit the user has since
    /// left. A monotonic token orders the builds instead.
    /// </summary>
    private long capsuleBuildGeneration;
    /// <summary>Full capsule state; null when the core is not connected.</summary>
    public async Task<JsonObject?> BuildCapsuleStateAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Capsule.BuildCapsuleStateAsync");
        var activeClient = client;
        if (activeClient is null)
        {
            return null;
        }

        var generation = ++capsuleBuildGeneration;
        var snapshot = playback;
        var trackId = snapshot.ActiveTrackId;
        var external = UsesCurrentPlaybackPresentation(trackId);
        var togetherScope = PlaybackPresentationScope;
        var sourceGeneration = remotePlayGeneration;
        var detail = CurrentTrackDetail;
        var title = string.Empty;
        var artist = string.Empty;
        var artwork = string.Empty;
        if (trackId is not null)
        {
            try
            {
                // Remote/Together playback need not exist in the local catalog.
                // Use the same metadata as the playback bar, including late covers.
                var track = external ? RemotePlaybackPresentation.WebDetail(detail, trackId, true) : await activeClient.SendAsync("desktop_catalog_get_track", new { trackId, includeArtwork = true }, CancellationToken.None);
                if (track.ValueKind == JsonValueKind.Object)
                {
                    title = ReadCapsuleString(track, "displayTitle") ?? ReadCapsuleString(track, "title") ?? string.Empty;
                    artist = ReadCapsuleString(track, "artist") ?? string.Empty;
                    artwork = ReadCapsuleString(track, "artwork") ?? string.Empty;
                }
                else if (external)
                {
                    title = PlaybackTitle;
                    artist = PlaybackArtist;
                    artwork = PlaybackArtwork ?? string.Empty;
                }
            }
            catch (Exception error)
            {
                OFPlayer_WinUI.Services.PreviewLog.Write($"capsule track failed: {error.GetType().Name}: {error.Message}");
            }
        }

        var lyricLine = string.Empty;
        var lyricIndex = -1;
        var lyricSource = string.Empty;
        var timeline = new JsonArray();
        if (trackId is not null)
        {
            try
            {
                var lyrics = await ResolvePlaybackLyricsAsync(trackId, snapshot.PositionSeconds);
                if (lyrics.ValueKind == JsonValueKind.Object && lyrics.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
                {
                    lyricSource = ReadCapsuleString(lyrics, "source") ?? string.Empty;
                    BuildCapsuleTimeline(lines, snapshot.PositionSeconds, lyricSource == "sidecar-ttml", out lyricLine, out lyricIndex, timeline);
                }
            }
            catch (Exception error)
            {
                OFPlayer_WinUI.Services.PreviewLog.Write($"capsule lyrics failed: {error.GetType().Name}: {error.Message}");
            }
        }

        // A slow track/lyrics lookup must not overwrite a newer build — including the
        // A → B → A case, where the track id matches again but the payload is stale.
        // Anchor the returned timeline to current playback rather than the pre-I/O position.
        if (generation != capsuleBuildGeneration || playback.ActiveTrackId != trackId || client != activeClient)
            return null;
        // Every local original has native ID "external". Fence A -> B -> A and
        // same-ID metadata/cover changes even before the next capsule build starts.
        if (external && (sourceGeneration != remotePlayGeneration || !ReferenceEquals(detail, CurrentTrackDetail)))
            return null;
        if (togetherScope != PlaybackPresentationScope)
            return null;
        snapshot = playback;
        var metaText = string.IsNullOrEmpty(artist) ? title : (string.IsNullOrEmpty(title) ? artist : $"{artist} / {title}");
        return new JsonObject
        {
            ["seq"] = ++capsuleSeq,
            ["trackId"] = trackId,
            ["hasTrack"] = trackId is not null,
            ["title"] = title,
            ["artist"] = artist,
            ["metaText"] = metaText,
            ["lyricLine"] = lyricLine,
            ["lyricText"] = lyricLine,
            ["lyricVersion"] = ++capsuleLyricVersion,
            ["lyricIndex"] = lyricIndex >= 0 ? JsonValue.Create(lyricIndex) : null,
            ["lyricSource"] = lyricSource,
            ["lyricTimeline"] = timeline,
            ["isPlaying"] = snapshot.Playing,
            ["durationMs"] = (long)(snapshot.DurationSeconds * 1000),
            ["positionMs"] = (long)(snapshot.PositionSeconds * 1000),
            ["sentAtMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["artworkKey"] = trackId,
            ["artworkSrc"] = artwork,
            ["artworkUrl"] = artwork,
            ["isLoading"] = false,
        };
    }

    /// <summary>Capsule page diagnostics forwarded to the host log (fire and forget).</summary>
    public async Task LogCapsuleDiagnosticsEventAsync(JsonElement request)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Capsule.LogCapsuleDiagnosticsEventAsync");
        var activeClient = client;
        if (activeClient is null)
        {
            return;
        }

        try
        {
            await activeClient.SendAsync("diagnostics_log_event", new { request }, CancellationToken.None);
        }
        catch (Exception error)
        {
            OFPlayer_WinUI.Services.PreviewLog.Write($"capsule diagnostics forward failed: {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>Light progress anchor (old `capsule://progress-anchor`).</summary>
    public JsonObject BuildCapsuleProgressAnchor()
    {
        var snapshot = playback;
        return new JsonObject
        {
            ["seq"] = ++capsuleSeq,
            ["trackId"] = snapshot.ActiveTrackId,
            ["isPlaying"] = snapshot.Playing,
            ["durationMs"] = (long)(snapshot.DurationSeconds * 1000),
            ["positionMs"] = (long)(snapshot.PositionSeconds * 1000),
            ["sentAtMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    /// <summary>
    /// Full per-track timeline so progress anchors can advance or seek without
    /// re-querying lyrics on every playback tick.
    ///
    /// R4-3: built from the shared <see cref = "LyricTimeline"/> so the capsule groups
    /// bilingual rows and resolves end times exactly like the native panel and the
    /// immersive page. Before this, the capsule emitted raw LRC lines (doubling every
    /// bilingual line) and pre-selected line 0 during a blank intro.
    /// </summary>
    private static void BuildCapsuleTimeline(JsonElement lines, double positionSeconds, bool isTtml, out string lyricLine, out int lyricIndex, JsonArray timeline)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("MainViewModel.Capsule.BuildCapsuleTimeline");
        var display = LyricTimeline.CreateDisplayLines(LyricHostPayload.ParseLines(lines), disableBilingualGrouping: isTtml);
        if (display.Count == 0)
        {
            lyricLine = string.Empty;
            lyricIndex = -1;
            return;
        }

        // Before the first timed line the capsule intentionally shows the first line
        // as upcoming text (its "not started" look), but reports no active index so
        // the page does not highlight anything during a blank intro.
        var active = OFPlayer.WinUI.Core.Lyrics.LyricTimeline.FindActiveIndex(display, positionSeconds);
        var current = active;
        if (current < 0)
        {
            // TTML may have true gaps after a line's explicit end. Show the next
            // upcoming line (or the tail after all lyrics) without marking active.
            var positionMs = LyricTimeline.ToMs(positionSeconds);
            current = Enumerable.Range(0, display.Count).FirstOrDefault(index => display[index].StartMs > positionMs, display.Count - 1);
        }

        lyricLine = display[current].Text;
        lyricIndex = active >= 0 ? display[current].Index : -1;
        for (var index = 0; index < display.Count; index++)
        {
            var entry = display[index];
            var words = new JsonArray();
            foreach (var word in entry.Words)
            {
                words.Add(new JsonObject { ["text"] = word.Text, ["startTime"] = word.StartSeconds is { } start ? JsonValue.Create(start) : null, ["endTime"] = word.EndSeconds is { } end ? JsonValue.Create(end) : null, });
            }

            timeline.Add(new JsonObject { ["index"] = entry.Index, ["text"] = entry.Text, ["startMs"] = entry.StartMs, ["endMs"] = entry.EndMs, ["translatedLyric"] = entry.Translation, ["romanLyric"] = entry.Roman, ["words"] = words, ["isBG"] = entry.IsBackground, ["isDuet"] = entry.IsDuet, ["agent"] = entry.Agent, });
        }
    }

    private static double? ReadCapsuleNumber(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    private static string? ReadCapsuleString(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
