namespace OFPlayer.WinUI.Core.Catalog;

public sealed record TrackNavMarker(int GroupIndex, int StartTrack, int EndTrack)
{
    public int StartIndex => StartTrack - 1;
    public int Count => EndTrack - StartTrack + 1;
}

public readonly record struct TrackNavScrollFrame(double ScrollTop, bool Done);

/// <summary>Global, zero-based positions for the track list navigation rail.</summary>
public static class TrackNavRailModel
{
    public const int GroupSize = 5;
    // One marker per group up to this many; past it, groups are sampled evenly. The rail
    // scrolls, so this bounds how much there is to scroll through, not how tall it is.
    public const int MaxMarkers = 120;
    // The rail shows at most this many markers at once and scrolls to the rest.
    public const int VisibleMarkers = 10;
    public const int MinTrackCount = 40;
    public const int ScrollMinDurationMs = 240;
    public const int ScrollMaxDurationMs = 560;
    public const int ScrollDurationDistancePx = 20_000;

    public static bool IsVisible(int totalCount) => totalCount > MinTrackCount;

    public static IReadOnlyList<TrackNavMarker> BuildMarkers(int totalCount, int maxMarkers = MaxMarkers)
    {
        if (totalCount <= 0) return Array.Empty<TrackNavMarker>();
        var groupCount = (int)(((long)totalCount + GroupSize - 1) / GroupSize);
        var markerCount = Math.Min(groupCount,
            Math.Max(groupCount > 1 ? 2 : 1, Math.Min(maxMarkers, MaxMarkers)));
        var markers = new List<TrackNavMarker>(markerCount);
        for (var index = 0; index < markerCount; index++)
        {
            var groupIndex = markerCount == 1 ? 0
                : (int)Math.Round((double)index * (groupCount - 1) / (markerCount - 1),
                    MidpointRounding.AwayFromZero);
            var start = groupIndex * GroupSize + 1;
            markers.Add(new TrackNavMarker(groupIndex, start,
                (int)Math.Min((long)start + GroupSize - 1, totalCount)));
        }
        return markers;
    }

    /// <summary>
    /// How many marker slots the rail shows, given how many fit beside the list. A rail with more
    /// markers than that stops half a slot short, so the cut-off marker shows there is more.
    /// </summary>
    public static double ViewportSlots(int markerCount, int fittingSlots)
    {
        var visible = Math.Clamp(fittingSlots, 2, VisibleMarkers);
        return markerCount <= visible ? Math.Max(0, markerCount) : visible - 0.5;
    }

    /// <summary>
    /// The rail's scroll offset that shows a marker: unchanged while the marker is wholly in view,
    /// otherwise the marker centred, within the rail's scroll range.
    /// </summary>
    public static double RevealOffset(int markerIndex, int markerCount, double slot, double viewport, double current)
    {
        var range = Math.Max(0, markerCount * slot - viewport);
        if (markerIndex < 0 || markerIndex >= markerCount) return Math.Clamp(current, 0, range);
        var top = markerIndex * slot;
        if (top >= current && top + slot <= current + viewport) return Math.Clamp(current, 0, range);
        return Math.Clamp(top - (viewport - slot) / 2, 0, range);
    }

    public static int ActiveMarkerIndex(IReadOnlyList<TrackNavMarker> markers, int globalRowIndex)
    {
        if (markers.Count == 0) return -1;
        var activeGroup = Math.Max(0, globalRowIndex) / GroupSize;
        var nearest = 0;
        var distance = long.MaxValue;
        for (var index = 0; index < markers.Count; index++)
        {
            var candidate = Math.Abs((long)markers[index].GroupIndex - activeGroup);
            if (candidate < distance)
            {
                nearest = index;
                distance = candidate;
            }
        }
        return nearest;
    }

    public static int ScrollDurationMs(double distancePx)
    {
        var distance = double.IsFinite(distancePx) ? Math.Abs(distancePx) : 0;
        var ratio = Math.Min(distance / ScrollDurationDistancePx, 1);
        return (int)Math.Round(ScrollMinDurationMs +
            ratio * (ScrollMaxDurationMs - ScrollMinDurationMs), MidpointRounding.AwayFromZero);
    }

    public static TrackNavScrollFrame ScrollFrame(
        double startScrollTop, double targetScrollTop, double elapsedMs, double durationMs)
    {
        var safeDuration = double.IsFinite(durationMs) && durationMs > 0 ? durationMs : 1;
        var safeElapsed = double.IsFinite(elapsedMs) ? elapsedMs : 0;
        var progress = Math.Clamp(safeElapsed / safeDuration, 0, 1);
        var easedProgress = EaseScroll(progress);
        return new TrackNavScrollFrame(
            startScrollTop + (targetScrollTop - startScrollTop) * easedProgress,
            progress >= 1);
    }

    // Match cubic-bezier(.22, 1, .36, 1). Progress is x, so solve x(t)
    // before sampling y(t); t itself is not elapsed time.
    private static double EaseScroll(double progress)
    {
        if (progress <= 0 || progress >= 1) return progress;
        var lower = 0.0;
        var upper = 1.0;
        for (var iteration = 0; iteration < 24; iteration++)
        {
            var t = (lower + upper) / 2;
            var inverse = 1 - t;
            var x = 3 * inverse * inverse * t * .22 + 3 * inverse * t * t * .36 + t * t * t;
            if (x < progress) lower = t;
            else upper = t;
        }
        return 1 - Math.Pow(1 - (lower + upper) / 2, 3);
    }
}
