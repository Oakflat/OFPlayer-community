namespace OFPlayer.WinUI.Core.Lyrics;

/// <summary>
/// Lyric timeline model — a direct, dependency-free port of the old front-end
/// model in `src/models/lyrics.ts`. Keeping it in Core (net10.0, no WinUI/Windows
/// dependency) means the grouping rules are unit-testable without a UI host.
///
/// Why this exists rather than "just render the raw lines": LRC files routinely
/// carry the original and its translation as two entries at the *same* timestamp.
/// Rendering raw lines therefore doubles every bilingual line. The old client
/// merged entries within <see cref="BilingualToleranceMs"/> into one display line
/// and folded the secondary text into the translation/roman tracks. WinUI must
/// apply the identical rule or bilingual lyrics visibly diverge from the old app.
/// </summary>
public static class LyricTimeline
{
    /// <summary>TTML word timing as delivered by the native host, in seconds.</summary>
    public readonly record struct Word(string Text, double? StartSeconds, double? EndSeconds);
    /// <summary>Entries starting within this window are treated as one display line (old `BILINGUAL_TIME_TOLERANCE_MS`).</summary>
    public const int BilingualToleranceMs = 80;

    /// <summary>An explicit endTime shorter than this is discarded as noise (old `MIN_EXPLICIT_LINE_DURATION_MS`).</summary>
    public const int MinExplicitLineDurationMs = BilingualToleranceMs + 1;

    /// <summary>Duration assumed for the last line when nothing else bounds it (old `fallbackLineMs`).</summary>
    public const int FallbackLineMs = 5000;

    /// <summary>One raw line as delivered by the host (`lyrics_resolve_track`).</summary>
    public readonly record struct InputLine(
        int Index,
        string Text,
        double? StartSeconds,
        double? EndSeconds,
        string Translation,
        string Roman)
    {
        public IReadOnlyList<Word> Words { get; init; } = Array.Empty<Word>();
        public bool IsBackground { get; init; }
        public bool IsDuet { get; init; }
        public string? Agent { get; init; }
    }

    /// <summary>One grouped display line (old `LyricPlayerLineModel`).</summary>
    public sealed record DisplayLine(
        int Index,
        string Text,
        double? StartSeconds,
        double? EndSeconds,
        string Translation,
        string Roman)
    {
        public IReadOnlyList<Word> Words { get; init; } = Array.Empty<Word>();
        public bool IsBackground { get; init; }
        public bool IsDuet { get; init; }
        public string? Agent { get; init; }
        public bool IsTtml { get; init; }
        public long StartMs => StartSeconds is { } seconds ? ToMs(seconds) : 0;

        /// <summary>
        /// Effective end in ms. The grouping pass already resolves this (explicit end
        /// clamped to the next start, else next start, else start + fallback), so this
        /// must not re-derive or clamp — doing so would silently override the caller's
        /// intent and diverge from the old model.
        /// </summary>
        public long EndMs => EndSeconds is { } seconds ? ToMs(seconds) : StartMs + FallbackLineMs;
    }

    /// <summary>Old `toLyricPlayerTimeMs`: floor to ms with a tiny epsilon nudge for float noise.</summary>
    public static long ToMs(double seconds) =>
        !double.IsFinite(seconds) || seconds <= 0 ? 0 : (long)Math.Floor(seconds * 1000 + 0.000001);

    /// <summary>
    /// Groups raw lines into display lines, folding near-simultaneous bilingual
    /// entries and resolving each line's effective end time.
    /// Mirrors `createLyricPlayerLines` in `src/models/lyrics.ts`.
    /// </summary>
    public static IReadOnlyList<DisplayLine> CreateDisplayLines(
        IEnumerable<InputLine> lines,
        int fallbackLineMs = FallbackLineMs,
        bool disableBilingualGrouping = false)
    {
        var entries = new List<(int Index, string Text, long StartMs, long? EndMs, string Translation, string Roman,
            IReadOnlyList<Word> Words, bool IsBackground, bool IsDuet, string? Agent)>();
        var position = 0;

        foreach (var line in lines)
        {
            // Unsynced lines carry no start; the old model spaces them evenly so a
            // plain-text lyric sheet still highlights progressively.
            var startMs = line.StartSeconds is { } start && double.IsFinite(start) && start >= 0
                ? ToMs(start)
                : (long)position * fallbackLineMs;

            long? endMs = line.EndSeconds is { } end && double.IsFinite(end) && end >= 0
                ? Math.Max(startMs + 1, ToMs(end))
                : null;

            entries.Add((line.Index, line.Text, startMs, endMs, line.Translation ?? string.Empty, line.Roman ?? string.Empty,
                line.Words, line.IsBackground, line.IsDuet, line.Agent));
            position++;
        }

        // Group by start time inside the tolerance window (the old client grouped
        // against the *previous group's* start, not the previous entry's).
        var groups = new List<(long StartMs, List<int> Members)>();

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var previous = groups.Count > 0 ? groups[^1] : default;

            // Background vocals and duet agents are parallel lyric lines, not
            // bilingual translations. Keep each in its own display row even at
            // exactly the same timestamp as the lead vocal.
            var previousEntry = groups.Count > 0 ? entries[previous.Members[0]] : default;
            if (!disableBilingualGrouping && groups.Count > 0 && !entry.IsBackground && !entry.IsDuet &&
                !previousEntry.IsBackground && !previousEntry.IsDuet &&
                Math.Abs(entry.StartMs - previous.StartMs) <= BilingualToleranceMs)
            {
                previous.Members.Add(i);
                continue;
            }

            groups.Add((entry.StartMs, new List<int> { i }));
        }

        var result = new List<DisplayLine>(groups.Count);

        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];

            // Primary = first member with real text (old model), else the first member.
            var members = group.Members.Select(m => entries[m]).ToList();
            var primarySlot = members.FindIndex(m => m.Text.Trim().Length > 0);

            if (primarySlot < 0)
            {
                primarySlot = 0;
            }

            var primary = members[primarySlot];
            // Parallel BG/duet rows do not end a lead row and vice versa.
            // Within each role, only a later start bounds the duration.
            var nextStartMs = groups.Skip(g + 1)
                .Where(next => next.StartMs > group.StartMs)
                .Where(next =>
                {
                    var candidate = entries[next.Members[0]];
                    return candidate.IsBackground == primary.IsBackground &&
                        candidate.IsDuet == primary.IsDuet &&
                        (!primary.IsDuet || candidate.Agent == primary.Agent);
                })
                .Select(next => (long?)next.StartMs)
                .FirstOrDefault();
            var secondary = members
                .Where((_, slot) => slot != primarySlot)
                .Where(m => m.Text.Trim().Length > 0)
                .ToList();

            var translations = new List<string>();
            var romans = new List<string>();

            if (primary.Translation.Length > 0)
            {
                translations.Add(primary.Translation);
            }

            if (primary.Roman.Length > 0)
            {
                romans.Add(primary.Roman);
            }

            foreach (var entry in secondary)
            {
                // The secondary entry's own text becomes a translation line — this is
                // how a two-language LRC collapses into one row with two tracks.
                translations.Add(entry.Text);

                if (entry.Translation.Length > 0)
                {
                    translations.Add(entry.Translation);
                }

                if (entry.Roman.Length > 0)
                {
                    romans.Add(entry.Roman);
                }
            }

            // Explicit end wins only when it is sane: long enough to be real and not
            // past the next line's start (otherwise it would swallow the next line).
            var explicitEnd = disableBilingualGrouping
                ? primary.EndMs is { } ttmlEnd && ttmlEnd > group.StartMs
                    ? ttmlEnd : (long?)null
                : group.Members
                .Select(m => entries[m].EndMs)
                .Where(v => v is not null)
                .Select(v => v!.Value)
                .Where(v => v >= group.StartMs + MinExplicitLineDurationMs)
                .Where(v => nextStartMs is null || v <= nextStartMs)
                .OrderBy(v => v)
                .Cast<long?>()
                .FirstOrDefault();

            var endMs = explicitEnd
                ?? (nextStartMs is not null
                    ? Math.Max(group.StartMs + 1, nextStartMs.Value)
                    : group.StartMs + fallbackLineMs);

            result.Add(new DisplayLine(
                primary.Index,
                primary.Text,
                group.StartMs / 1000.0,
                endMs / 1000.0,
                JoinUnique(translations),
                JoinUnique(romans))
            {
                Words = primary.Words,
                IsBackground = primary.IsBackground,
                IsDuet = primary.IsDuet,
                Agent = primary.Agent,
                IsTtml = disableBilingualGrouping,
            });
        }

        return result;
    }

    /// <summary>
    /// Active display-line index for a playback position (old `findActiveLyricPlayerLineIndex`).
    /// Returns -1 before the first timed line — deliberately, so an intro blank
    /// segment highlights nothing instead of pre-highlighting line 0.
    /// </summary>
    public static int FindActiveIndex(IReadOnlyList<DisplayLine> lines, double positionSeconds)
    {
        if (lines.Count == 0 || !double.IsFinite(positionSeconds) || positionSeconds < 0)
        {
            return -1;
        }

        var currentMs = ToMs(positionSeconds);
        if (lines.Any(line => line.IsTtml))
        {
            // TTML can overlap lines and explicitly ends them. A short BG entry
            // must not displace an ongoing lead vocal, even if it starts later.
            var live = Enumerable.Range(0, lines.Count)
                .Where(i => lines[i].StartMs <= currentMs && currentMs < lines[i].EndMs)
                .OrderBy(i => lines[i].IsBackground || lines[i].IsDuet ? 1 : 0)
                .ThenByDescending(i => lines[i].StartMs)
                .FirstOrDefault(-1);
            return live;
        }
        var index = -1;
        long latestStart = -1;

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartMs <= currentMs)
            {
                if (lines[i].StartMs > latestStart)
                {
                    index = i;
                    latestStart = lines[i].StartMs;
                }
                else if (lines[i].StartMs == latestStart && index >= 0 &&
                    (lines[index].IsBackground || lines[index].IsDuet) &&
                    !lines[i].IsBackground && !lines[i].IsDuet)
                {
                    index = i;
                }
            }
            else
            {
                break;
            }
        }

        return index;
    }

    /// <summary>Old `uniqueNonEmptyText`: trim, drop blanks, preserve first-seen order.</summary>
    private static string JoinUnique(List<string> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<string>();

        foreach (var item in items)
        {
            var value = item.Trim();

            if (value.Length == 0 || !seen.Add(value))
            {
                continue;
            }

            kept.Add(value);
        }

        return string.Join('\n', kept);
    }
}
