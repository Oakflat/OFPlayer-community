namespace OFPlayer.WinUI.Core.Playback;

/// <summary>Queue membership has its own revision: editing it never advances or reloads audio.</summary>
public sealed class QueueObservationOrder
{
    public ulong? Revision { get; private set; }
    private IReadOnlyList<string> ids = Array.Empty<string>();

    public void Reset() { Revision = null; ids = Array.Empty<string>(); }

    public bool TryAccept(ulong? incoming, IReadOnlyList<string> queue)
    {
        if (Revision is ulong current && (incoming is not ulong next || next < current ||
            next == current && !ids.SequenceEqual(queue))) return false;
        Revision = incoming;
        ids = queue.ToArray();
        return true;
    }

    /// <summary>Preserve the existing shuffled order when membership changes.</summary>
    public static List<string> ReconcileShuffle(IEnumerable<string> order, IReadOnlyList<string> queue)
    {
        var available = queue.ToHashSet(StringComparer.Ordinal);
        var retained = PlaybackOrder.Normalize(order).Where(available.Contains).ToList();
        var seen = retained.ToHashSet(StringComparer.Ordinal);
        retained.AddRange(queue.Where(seen.Add));
        return retained;
    }

    /// <summary>Project only a successful native edit into the existing shuffle order.</summary>
    public static List<string> ApplyEdit(IEnumerable<string> order, IReadOnlyList<string> queue,
        string action, IReadOnlyList<string> edited, string? anchor)
    {
        var result = ReconcileShuffle(order, queue);
        if (action == "remove") return result;
        if (action is not ("next" or "append")) throw new ArgumentException("Unknown queue edit.", nameof(action));
        var moving = PlaybackOrder.Normalize(edited).Where(id => id != anchor && queue.Contains(id)).ToList();
        var moved = moving.ToHashSet(StringComparer.Ordinal);
        result.RemoveAll(moved.Contains);
        var index = action == "next" ? result.IndexOf(anchor ?? "") + 1 : result.Count;
        result.InsertRange(index, moving);
        return result;
    }
}
