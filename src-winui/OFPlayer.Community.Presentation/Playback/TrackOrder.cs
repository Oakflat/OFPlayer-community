namespace OFPlayer.WinUI.Core.Playback;

/// <summary>Merge a completed drag into the full order, retaining filtered and unpaged rows.</summary>
public static class TrackOrder
{
    public static IReadOnlyList<string> Merge(
        IReadOnlyList<string> fullOrder,
        IReadOnlyList<string> beforeVisible,
        IReadOnlyList<string> afterVisible)
    {
        var visible = beforeVisible.ToHashSet(StringComparer.Ordinal);
        if (visible.Count != beforeVisible.Count || afterVisible.Count != beforeVisible.Count ||
            !visible.SetEquals(afterVisible) || afterVisible.Distinct(StringComparer.Ordinal).Count() != afterVisible.Count ||
            !visible.IsSubsetOf(fullOrder))
            throw new InvalidOperationException("列表内容已变化，请重新拖动排序。");

        var next = 0;
        return fullOrder.Select(id => visible.Contains(id) ? afterVisible[next++] : id).ToArray();
    }
}
