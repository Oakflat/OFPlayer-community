using Windows.Media.Core;
using Windows.UI;

namespace OFPlayer.WindowsMedia;
public sealed record VideoSubtitleLine(int Index, double Position, double Duration, string Text);
public sealed record VideoSubtitleExportCue(TimeSpan Start, TimeSpan End, string Text);
public sealed record VideoSubtitleExportSnapshot(long Generation, string VideoPath, string SubtitlePath,
    double Delay, IReadOnlyList<VideoSubtitleExportCue> Cues);

public sealed partial class WindowsVideoBackend
{
    private sealed record CueLook(TimedTextCue Cue, TimedTextStyle? Style, TimedTextRegion? Region);
    private sealed record CueTiming(TimedTextCue Cue, TimeSpan Start, TimeSpan Duration);
    private sealed class SubtitleStyleState(CueLook[] originals)
    {
        public CueLook[] Originals { get; } = originals;
        public VideoSubtitleAppearance? Applied { get; set; }
        public CueTiming[] Times { get; } = originals.Select(item => new CueTiming(item.Cue, item.Cue.StartTime, item.Cue.Duration)).ToArray();
        public TimedTextCue[] VisibleCues { get; set; } = originals.Select(item => item.Cue).ToArray();
        public double Delay { get; set; }
    }
    private static TimedTextStyle CopyStyle(TimedTextStyle s) => new() {
        Name=s.Name, FontFamily=s.FontFamily, FontSize=s.FontSize, FontWeight=s.FontWeight,
        FontStyle=s.FontStyle, FontAngleInDegrees=s.FontAngleInDegrees, Foreground=s.Foreground,
        Background=s.Background, IsBackgroundAlwaysShown=s.IsBackgroundAlwaysShown,
        FlowDirection=s.FlowDirection, LineAlignment=s.LineAlignment,
        OutlineColor=s.OutlineColor, OutlineRadius=s.OutlineRadius, OutlineThickness=s.OutlineThickness,
        IsLineThroughEnabled=s.IsLineThroughEnabled, IsOverlineEnabled=s.IsOverlineEnabled,
        IsUnderlineEnabled=s.IsUnderlineEnabled, IsTextCombined=s.IsTextCombined
    };
    private static TimedTextRegion CopyRegion(TimedTextRegion r) => new() {
        Name=r.Name, Background=r.Background, DisplayAlignment=r.DisplayAlignment, Extent=r.Extent,
        IsOverflowClipped=r.IsOverflowClipped, LineHeight=r.LineHeight, Padding=r.Padding,
        Position=r.Position, TextWrapping=r.TextWrapping, WritingMode=r.WritingMode, ZIndex=r.ZIndex, ScrollMode=r.ScrollMode
    };
    private static CueLook SnapshotLook(TimedTextCue cue) => new(cue,
        cue.CueStyle is { } style ? CopyStyle(style) : null,
        cue.CueRegion is { } region ? CopyRegion(region) : null);
    private static void RestoreLook(CueLook look)
    {
        // Every cue receives independent objects; never mutate shared parser-owned style/region objects.
        look.Cue.CueStyle = look.Style is { } style ? CopyStyle(style) : null;
        look.Cue.CueRegion = look.Region is { } region ? CopyRegion(region) : null;
    }
    private static void RegisterPlainSubtitleStyle(Load scope, TimedMetadataTrack track)
    {
        if (scope.SoftwareSource is not null || track.Cues.Count == 0) return;
        var cues = track.Cues.OfType<TimedTextCue>().ToArray();
        var canStyle = cues.Length == track.Cues.Count && cues.Length <= 10000 && !cues.Any(cue => cue.Lines.Count == 0 || cue.Lines.Any(line => line.Subformats.Count > 0));
        try {
            if (canStyle) scope.SubtitleStyles[track] = new(cues.Select(SnapshotLook).ToArray());
            if (scope.SourceOwner is WindowsContinuousSource { TimeScale: not 1 } source) RescaleSubtitleTrack(scope, track, 1, source.TimeScale);
        }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine($"Subtitle styling unavailable: {error.Message}"); }
    }

    private static void RescaleSubtitleClock(Load scope, double oldScale, double newScale)
    {
        List<TimedMetadataTrack> completed = [];
        try {
            foreach (var track in scope.Item.TimedMetadataTracks) {
                RescaleSubtitleTrack(scope, track, oldScale, newScale);
                completed.Add(track);
            }
        }
        catch {
            foreach (var track in completed.AsEnumerable().Reverse()) RescaleSubtitleTrack(scope, track, newScale, oldScale);
            throw;
        }
    }
    private static void RescaleSubtitleTrack(Load scope, TimedMetadataTrack track, double oldScale, double newScale)
    {
        if (oldScale == newScale) return;
        var before = track.Cues.OfType<TimedTextCue>().Select(cue => new CueTiming(cue, cue.StartTime, cue.Duration)).ToArray();
        static CueTiming Scale(CueTiming timing, double factor, double offset) {
            var start = timing.Start.Ticks * factor + offset;
            var end = start + timing.Duration.Ticks * factor;
            var visibleStart = Math.Max(0, start);
            return new(timing.Cue, TimeSpan.FromTicks((long)Math.Round(visibleStart)), TimeSpan.FromTicks((long)Math.Round(Math.Max(0, end - visibleStart))));
        }
        var next = scope.SubtitleStyles.TryGetValue(track, out var style)
            ? style.Times.Select(t => Scale(t, 1 / newScale, TimeSpan.FromSeconds(style.Delay).Ticks / newScale)).ToArray()
            : before.Select(t => Scale(t, oldScale / newScale, 0)).ToArray();
        try {
            foreach (var timing in before) track.RemoveCue(timing.Cue);
            foreach (var timing in next) { timing.Cue.StartTime = timing.Start; timing.Cue.Duration = timing.Duration; if (timing.Duration > TimeSpan.Zero) track.AddCue(timing.Cue); }
            if (style is not null) style.VisibleCues = next.Where(t => t.Duration > TimeSpan.Zero).Select(t => t.Cue).ToArray();
        }
        catch {
            foreach (var cue in track.Cues.OfType<TimedTextCue>().ToArray()) track.RemoveCue(cue);
            foreach (var timing in before) { timing.Cue.StartTime = timing.Start; timing.Cue.Duration = timing.Duration; track.AddCue(timing.Cue); }
            throw;
        }
    }
    private SubtitleStyleState? CurrentSubtitleStyle()
    {
        if (!loaded || active is not { SoftwareSource: null } scope || SelectedSubtitle(scope) is not { } selected
            || !scope.SubtitleStyles.TryGetValue(selected, out var state)) return null;
        // Count alone misses replacement of cues by a dynamic provider.
        var actualCues = selected.Cues.ToHashSet();
        if (actualCues.Count != state.VisibleCues.Length || state.VisibleCues.Any(cue => !actualCues.Contains(cue))) {
            scope.SubtitleStyles.Remove(selected);
            return null;
        }
        return state;
    }
    private void RollBackSubtitleLooks(SubtitleStyleState state, CueLook[] before)
    {
        var failed = false;
        foreach (var look in before) { try { RestoreLook(look); } catch { failed = true; } }
        if (!failed) return;
        if (active is { } scope && SelectedSubtitle(scope) is { } selected) scope.SubtitleStyles.Remove(selected);
        state.Applied = null;
        throw new InvalidOperationException("字幕外观恢复失败；已关闭该轨道外观编辑，请重新载入外挂字幕。");
    }

    private void ApplySubtitleStyle(VideoSubtitleAppearance appearance)
    {
        var state = CurrentSubtitleStyle() ?? throw new NotSupportedException("这条字幕的外观不能调整，请选择外挂的文字字幕。");
        var scope = active!;
        var selected = SelectedSubtitle(scope)!;
        if (selected.Cues.Count != state.VisibleCues.Length) {
            scope.SubtitleStyles.Remove(selected);
            throw new NotSupportedException("字幕内容已动态变化，已关闭外观编辑能力。");
        }
        var before = state.Originals.Select(look => SnapshotLook(look.Cue)).ToArray();
        try {
            foreach (var original in state.Originals) {
                var style = original.Style is { } savedStyle ? CopyStyle(savedStyle) : new TimedTextStyle();
                if (appearance.FontFamily.Length > 0) style.FontFamily = appearance.FontFamily;
                style.FontSize = new TimedTextDouble { Value = 4.5 * appearance.FontScale, Unit = TimedTextUnit.Percentage };
                style.Foreground = appearance.Color == "yellow" ? Color.FromArgb(255,255,255,0) : Color.FromArgb(255,255,255,255);
                style.OutlineColor = Color.FromArgb(255,0,0,0);
                style.OutlineThickness = new TimedTextDouble { Value = .15, Unit = TimedTextUnit.Percentage };
                style.LineAlignment = TimedTextLineAlignment.Center;
                var height = Math.Clamp(12 * appearance.FontScale, 12, 30);
                var region = original.Region is { } savedRegion ? CopyRegion(savedRegion) : new TimedTextRegion();
                region.Position = new TimedTextPoint { X=5, Y=Math.Clamp(appearance.VerticalPosition-height/2,0,100-height), Unit=TimedTextUnit.Percentage };
                region.Extent = new TimedTextSize { Width=90, Height=height, Unit=TimedTextUnit.Percentage };
                region.DisplayAlignment = TimedTextDisplayAlignment.Center;
                region.IsOverflowClipped = false;
                original.Cue.CueStyle = style;
                original.Cue.CueRegion = region;
            }
            state.Applied = appearance;
        }
        catch {
            RollBackSubtitleLooks(state, before);
            throw;
        }
    }
    public void ResetSubtitleAppearance()
    {
        CheckThread();
        var state = CurrentSubtitleStyle() ?? throw new NotSupportedException("当前字幕没有可恢复的外观设置。");
        var before = state.Originals.Select(look => SnapshotLook(look.Cue)).ToArray();
        try {
            foreach (var original in state.Originals) RestoreLook(original);
            state.Applied = null;
        }
        catch {
            RollBackSubtitleLooks(state, before);
            throw;
        }
        Publish();
    }
    // Re-register cues so the native scheduler sees the new timing. Never cumulatively
    // shift the already-adjusted timestamps; retain original timing per selected track.
    private void ApplyPlainSubtitleDelay(double seconds)
    {
        var state = CurrentSubtitleStyle() ?? throw new NotSupportedException("请先选择外挂的文字字幕。");
        var track = SelectedSubtitle(active!)!;
        var offset = TimeSpan.FromSeconds(seconds);
        var timeScale = (active!.SourceOwner as WindowsContinuousSource)?.TimeScale ?? 1;
        var next = state.Times.Select(t => {
            var start = t.Start + offset;
            var end = start + t.Duration;
            var visibleStart = start < TimeSpan.Zero ? TimeSpan.Zero : start;
            var visibleDuration = end <= TimeSpan.Zero ? TimeSpan.Zero : end - visibleStart;
            return new CueTiming(t.Cue, TimeSpan.FromTicks((long)Math.Round(visibleStart.Ticks / timeScale)), TimeSpan.FromTicks((long)Math.Round(visibleDuration.Ticks / timeScale)));
        }).ToArray();
        var before = state.Times.Select(t => new CueTiming(t.Cue, t.Cue.StartTime, t.Cue.Duration)).ToArray();
        var visibleBefore = state.VisibleCues;
        try {
            foreach (var cue in visibleBefore) track.RemoveCue(cue);
            foreach (var timing in next) {
                timing.Cue.StartTime = timing.Start;
                timing.Cue.Duration = timing.Duration;
                if (timing.Duration > TimeSpan.Zero) track.AddCue(timing.Cue);
            }
            state.VisibleCues = next.Where(t => t.Duration > TimeSpan.Zero).Select(t => t.Cue).ToArray();
            state.Delay = seconds;
        }
        catch {
            try {
                foreach (var cue in track.Cues.ToArray()) track.RemoveCue(cue);
                foreach (var timing in before) { timing.Cue.StartTime = timing.Start; timing.Cue.Duration = timing.Duration; }
                foreach (var cue in visibleBefore) track.AddCue(cue);
            }
            catch {
                active!.SubtitleStyles.Remove(track);
                throw new InvalidOperationException("字幕时序恢复失败，请重新加载外挂字幕后重试。");
            }
            throw;
        }
    }
    // Copy the complete supported track before any picker/await. No live cue references escape.
    public VideoSubtitleExportSnapshot CaptureSubtitleExport()
    {
        CheckThread();
        var state = CurrentSubtitleStyle() ?? throw new NotSupportedException("请选择可读取的外挂纯文本字幕。");
        var scope = active!;
        var track = SelectedSubtitle(scope)!;
        if (!scope.ExternalFiles.TryGetValue(track, out var subtitlePath))
            throw new NotSupportedException("目前仅支持导出外挂纯文本字幕。");
        var offset = TimeSpan.FromSeconds(state.Delay);
        var cues = state.Times.Select(t => new VideoSubtitleExportCue(t.Start + offset,
            t.Start + t.Duration + offset, string.Join("\n", t.Cue.Lines.Select(line => line.Text)))).ToArray();
        return new(scope.Generation, scope.Location, subtitlePath.OriginalPath, state.Delay, Array.AsReadOnly(cues));
    }
    public IReadOnlyList<VideoSubtitleLine> GetSubtitleLines(string? search = null, int limit = 200)
    {
        CheckThread();
        var state = CurrentSubtitleStyle();
        if (state is null) return [];
        search = search?.Trim() ?? "";
        return state.Times.Select((timing, index) => new VideoSubtitleLine(index,
            Math.Max(0, timing.Start.TotalSeconds + state.Delay), timing.Duration.TotalSeconds,
            string.Join("\n", timing.Cue.Lines.Select(line => line.Text))))
            .Where(line => search.Length == 0 || line.Text.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .Take(Math.Clamp(limit, 1, 1000)).ToArray();
    }
    public VideoSubtitleLine? GetAdjacentSubtitleLine(double position, bool forward)
    {
        CheckThread();
        var state = CurrentSubtitleStyle();
        if (state is null || !double.IsFinite(position) || position < 0) return null;
        // Skip cues entirely before time zero, and coalesce simultaneous starts for navigation.
        var candidates = state.Times.Select((timing, index) => new VideoSubtitleLine(index,
            Math.Max(0, timing.Start.TotalSeconds + state.Delay), timing.Duration.TotalSeconds,
            string.Join("\n", timing.Cue.Lines.Select(line => line.Text))))
            .Where(line => state.Times[line.Index].Start.TotalSeconds + state.Delay + line.Duration > 0);
        return forward ? candidates.Where(line => line.Position > position + .1).OrderBy(line => line.Position).FirstOrDefault()
            : candidates.Where(line => line.Position < position - .1).OrderByDescending(line => line.Position).FirstOrDefault();
    }
    public void AlignSubtitleLine(int index, double position)
    {
        CheckThread();
        var state = CurrentSubtitleStyle() ?? throw new NotSupportedException("当前字幕不能对时。");
        if (index < 0 || index >= state.Times.Length || !double.IsFinite(position) || position < 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        SetSubtitleDelay(position - state.Times[index].Start.TotalSeconds);
    }
    private string SubtitleStyleNotice()
        => CurrentSubtitleStyle() is { Applied: not null } ? "已覆盖当前外挂纯文本字幕外观；可恢复原样"
            : CurrentSubtitleStyle() is not null ? "保留原样；修改控件后仅覆盖当前外挂纯文本字幕"
            : "外观调整适用于外挂的文字字幕；内嵌、图片和特效字幕保持原样";
}
