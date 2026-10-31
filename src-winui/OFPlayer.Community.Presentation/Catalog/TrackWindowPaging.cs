namespace OFPlayer.WinUI.Core.Catalog;

/// <summary>Global offsets for a contiguous displayed track window.</summary>
public static class TrackWindowPaging
{
    public static int PageStart(int globalIndex, int pageSize) =>
        pageSize > 0 && globalIndex >= 0 ? globalIndex / pageSize * pageSize
            : throw new ArgumentOutOfRangeException();

    public static int PreviousStart(int windowOffset, int pageSize) =>
        pageSize > 0 && windowOffset >= 0 ? Math.Max(0, windowOffset - pageSize)
            : throw new ArgumentOutOfRangeException();

    public static int NextOffset(int windowOffset, int loadedCount) =>
        windowOffset >= 0 && loadedCount >= 0 && (long)windowOffset + loadedCount <= int.MaxValue
            ? windowOffset + loadedCount : throw new ArgumentOutOfRangeException();

    public static bool HasMore(int windowOffset, int loadedCount, int totalCount) =>
        NextOffset(windowOffset, loadedCount) < totalCount;
}
