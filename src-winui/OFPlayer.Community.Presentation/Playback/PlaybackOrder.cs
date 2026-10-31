namespace OFPlayer.WinUI.Core.Playback;

/// <summary>
/// Port of the old front-end `playbackOrder.ts`: queue normalization, shuffled
/// order and next/previous resolution driven by repeat/shuffle preferences.
///
/// Pure functions only (no clock, no IO, no UI), so the N2 playback semantics
/// stay unit-testable in Core and identical to the Tauri front-end:
/// - `normalize` trims/needs unique ids, keeping order;
/// - `createShuffled` keeps the anchor first and shuffles the remainder;
/// - `resolve` returns the next track id for user skips and ended events
///   (repeat=one replays only on `ended`; repeat=none stops at the edges;
///   shuffle forces repeat=none at the call site, matching the front-end).
/// </summary>
public static class PlaybackOrder
{
    public const string RepeatNone = "none";
    public const string RepeatAll = "all";
    public const string RepeatOne = "one";

    public const string ReasonUser = "user";
    public const string ReasonEnded = "ended";

    private const char SignatureSeparator = '\u001f';

    public static List<string> Normalize(IEnumerable<string?>? trackIds)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>();

        foreach (var trackId in trackIds ?? Enumerable.Empty<string?>())
        {
            var trimmed = trackId?.Trim();

            if (string.IsNullOrEmpty(trimmed) || !seen.Add(trimmed))
            {
                continue;
            }

            normalized.Add(trimmed);
        }

        return normalized;
    }

    public static string Signature(IEnumerable<string?>? trackIds) =>
        string.Join(SignatureSeparator, Normalize(trackIds));

    public static List<string> CreateShuffled(
        IEnumerable<string?>? trackIds,
        string? anchorTrackId = null,
        Func<int, int>? nextIndex = null)
    {
        var normalized = Normalize(trackIds);
        var anchor = anchorTrackId?.Trim() ?? string.Empty;
        var remaining = normalized.Where(trackId => trackId != anchor).ToList();
        var pick = nextIndex ?? CreateRandomPicker();

        // Fisher-Yates on the remainder, same as the old front-end.
        for (var index = remaining.Count - 1; index > 0; index--)
        {
            var swapIndex = pick(index + 1);
            (remaining[index], remaining[swapIndex]) = (remaining[swapIndex], remaining[index]);
        }

        if (anchor.Length > 0 && normalized.Contains(anchor))
        {
            remaining.Insert(0, anchor);
        }

        return remaining;
    }

    /// <summary>
    /// Returns the next track id in the given order, mirroring
    /// `resolvePlaybackOrderTrackId` in the old front-end.
    /// </summary>
    public static string? Resolve(
        IEnumerable<string?>? queueTrackIds,
        string? currentTrackId,
        string repeatMode = RepeatAll,
        int step = 1,
        string reason = ReasonUser)
    {
        var queue = Normalize(queueTrackIds);

        if (queue.Count == 0)
        {
            return null;
        }

        var active = currentTrackId?.Trim() ?? string.Empty;

        if (reason == ReasonEnded && repeatMode == RepeatOne && active.Length > 0)
        {
            return active;
        }

        var currentIndex = active.Length > 0 ? queue.IndexOf(active) : -1;

        if (currentIndex == -1)
        {
            return queue[0];
        }

        var nextIndex = currentIndex + step;

        if (nextIndex >= 0 && nextIndex < queue.Count)
        {
            return queue[nextIndex];
        }

        if (repeatMode == RepeatNone)
        {
            return null;
        }

        return queue[((nextIndex % queue.Count) + queue.Count) % queue.Count];
    }

    private static Func<int, int> CreateRandomPicker()
    {
        var random = new Random();

        return bound => random.Next(bound);
    }
}
