namespace OFPlayer.WinUI.Core.Remote;

/// <summary>
/// Provider-facing remote library descriptor. Mirrors the host contract
/// <c>ExternalRemoteLibrary</c> (id / name / rootPath).
///
/// Semantics worth pinning down (verified against the host and the old client):
/// for Subsonic the <see cref="Id"/> *is* the <c>musicFolderId</c> and the host
/// copies it into <c>rootPath</c>; for WebDAV there is exactly one library whose
/// id is the connection's root path. Selecting a library therefore means
/// re-issuing the listing with that id as the connection's <c>rootPath</c>.
/// </summary>
public readonly record struct RemoteLibraryDescriptor(string Id, string Name, string RootPath)
{
    public static RemoteLibraryDescriptor FromHost(string? id, string? name, string? rootPath)
    {
        var resolvedId = string.IsNullOrWhiteSpace(id) ? string.Empty : id.Trim();
        var resolvedName = string.IsNullOrWhiteSpace(name) ? resolvedId : name.Trim();
        var resolvedRoot = string.IsNullOrWhiteSpace(rootPath) ? resolvedId : rootPath.Trim();

        return new RemoteLibraryDescriptor(resolvedId, resolvedName, resolvedRoot);
    }

    /// <summary>The value to place on the connection's <c>rootPath</c> for this library.</summary>
    public string ConnectionRootPath => string.IsNullOrWhiteSpace(RootPath) ? Id : RootPath;

    public bool IsValid => !string.IsNullOrWhiteSpace(Id);
}

/// <summary>Why a listing round stopped, so the UI can tell the truth about it.</summary>
public enum RemotePagingOutcome
{
    /// <summary>The listing returned everything the provider had (no truncation signal).</summary>
    Complete,

    /// <summary>The host reported <c>truncated</c>: it stopped at the requested limit.</summary>
    Truncated,

    /// <summary>Nothing to list yet (no library selected / empty library).</summary>
    Idle,
}

/// <summary>
/// Remote listing pagination state.
///
/// Important honesty constraint: the host contract exposes **no offset/cursor**.
/// It only accepts <c>limit</c> and answers with <c>truncated</c>. So "loading
/// more" can only ever mean "ask again with a larger limit" - the provider
/// re-sends everything it previously sent. This type models exactly that, and
/// deliberately never invents a <c>total</c>: the previous UI displayed a
/// library count as if it were a track count.
/// </summary>
public sealed class RemotePagingState
{
    /// <summary>First page size. Large enough to be useful, small enough to be fast.</summary>
    public const int DefaultPageSize = 200;

    /// <summary>
    /// Hard cap on the repeated-limit strategy. Beyond this the re-fetch cost of
    /// "grow the limit again" outweighs the benefit, and the user should be told
    /// to narrow the library instead of silently hammering the server.
    /// </summary>
    public const int MaxPageSize = 5000;

    public RemotePagingState(int pageSize = DefaultPageSize)
    {
        PageSize = pageSize < 1 ? DefaultPageSize : pageSize;
    }

    public int PageSize { get; set; }

    /// <summary>Limit to send on the next request.</summary>
    public int RequestedLimit { get; private set; }

    public RemotePagingOutcome Outcome { get; private set; } = RemotePagingOutcome.Idle;

    /// <summary>Items accepted from the last successful round (not the provider total).</summary>
    public int ItemCount { get; private set; }

    /// <summary>True when the provider signalled truncation at the requested limit.</summary>
    public bool HasMore => Outcome == RemotePagingOutcome.Truncated;

    /// <summary>True when the cap is reached, so "load more" must stop being offered.</summary>
    public bool CapReached => RequestedLimit >= MaxPageSize;

    /// <summary>
    /// The limit for a fresh listing (first open, library switch, or refresh).
    /// Always resets: switching libraries must not inherit the previous library's
    /// grown limit.
    /// </summary>
    public int Reset()
    {
        RequestedLimit = PageSize;
        Outcome = RemotePagingOutcome.Idle;
        ItemCount = 0;
        return RequestedLimit;
    }

    /// <summary>
    /// The limit for the next round. Returns <c>null</c> when there is nothing to
    /// grow to (already complete, already capped, or never started).
    /// </summary>
    public int? Grow()
    {
        if (RequestedLimit <= 0 || !HasMore || CapReached)
        {
            return null;
        }

        var next = Math.Min(RequestedLimit + PageSize, MaxPageSize);
        return next == RequestedLimit ? null : next;
    }

    /// <summary>
    /// Folds a completed round into the state. <paramref name="providerTruncated"/>
    /// must come from the host's own <c>truncated</c> flag - never inferred from
    /// "the count equals the limit", which would be a guess.
    /// </summary>
    public void Apply(int requestedLimit, int itemCount, bool providerTruncated)
    {
        RequestedLimit = requestedLimit;
        ItemCount = itemCount < 0 ? 0 : itemCount;
        Outcome = ItemCount == 0
            ? RemotePagingOutcome.Idle
            : providerTruncated
                ? RemotePagingOutcome.Truncated
                : RemotePagingOutcome.Complete;
    }
}

/// <summary>
/// Identifies a remote listing request so late results can be discarded.
///
/// The task package calls out "迟到页" and "不靠重名识别连接". A connection id
/// plus a monotonically increasing generation covers both: two connections that
/// share a name never collide, and a page that arrives after the user switched
/// library (or connection) is recognised as stale even when the trim size matches.
/// </summary>
public readonly record struct RemoteRequestToken(string ConnectionId, string LibraryId, int Generation)
{
    /// <summary>True when <paramref name="other"/> supersedes this token.</summary>
    public bool IsStaleFor(RemoteRequestToken other) =>
        !string.Equals(ConnectionId, other.ConnectionId, StringComparison.Ordinal) ||
        !string.Equals(LibraryId, other.LibraryId, StringComparison.Ordinal) ||
        Generation != other.Generation;

    public override string ToString() => $"{ConnectionId}|{LibraryId}|{Generation}";
}

/// <summary>
/// Monotonic token source. Kept separate from the view model so the staleness
/// rule can be unit-tested without a UI thread.
/// </summary>
public sealed class RemoteRequestTokenSource
{
    private int generation;

    /// <summary>Issues the next token for a connection/library pair.</summary>
    public RemoteRequestToken Issue(string connectionId, string libraryId) =>
        new(connectionId ?? string.Empty, libraryId ?? string.Empty, ++generation);

    /// <summary>The generation that is currently current (0 before anything was issued).</summary>
    public int Current => generation;

    /// <summary>Invalidates every outstanding token (used on close / teardown).</summary>
    public void Invalidate() => generation++;
}

/// <summary>
/// Merge rules for a re-fetched listing.
///
/// Because the host has no cursor, a "load more" round returns a superset of the
/// previous round. Replacing the list wholesale is correct and keeps ordering
/// authoritative (the provider's own order). Append-dedup would silently preserve
/// a stale prefix if the remote library changed in between.
/// </summary>
public static class RemoteTrackMerge
{
    /// <summary>
    /// True when a re-fetch produced fewer items than the previous round, which
    /// means the remote library shrank (files deleted) and the grown limit bought
    /// us nothing. Surfacing this prevents an infinite "load more" loop.
    /// </summary>
    public static bool Shrank(int previousCount, int newCount) => newCount < previousCount;

    /// <summary>
    /// True when a grown limit returned exactly as many items as before while the
    /// provider still claims truncation - the remote library is being listed in a
    /// stable, capped order and growing cannot make progress.
    /// </summary>
    public static bool MadeNoProgress(int previousCount, int newCount, bool providerTruncated) =>
        providerTruncated && newCount > 0 && newCount == previousCount;
}
