namespace Attendance.Application.Abstractions;

/// <summary>
/// One page of a list, with enough to draw the controls around it.
/// </summary>
/// <typeparam name="T">What the page holds.</typeparam>
/// <remarks>
/// <para>
/// <b>Why paging exists here at all.</b> Several portal lists were written to
/// return everything: every employee, every device, every location. That is
/// fine on a system with four employees and becomes a page nobody can use, and
/// a query nobody wants, on a system with four thousand. The limit belongs in
/// the stored procedure, not in the view, because a view that renders the first
/// twenty rows of ten thousand has already paid for all ten thousand.
/// </para>
/// <para>
/// <b>Offset paging, deliberately, and not everywhere.</b> <c>OFFSET</c>
/// degrades as the depth grows, which is why
/// <c>admin.usp_AuditLog_Search</c> pages by key instead: an audit log is
/// append-only, unbounded and read from the newest end. The lists here are
/// different — they are bounded by the size of the organisation, they are
/// sorted by name rather than by time, and an administrator expects to jump to
/// a page and to be told how many there are. Keyset paging cannot offer either.
/// </para>
/// </remarks>
public sealed class PagedResult<T>
{
    /// <summary>Creates a page.</summary>
    /// <param name="items">The rows on this page.</param>
    /// <param name="page">The 1-based page number.</param>
    /// <param name="pageSize">How many rows a full page holds.</param>
    /// <param name="totalCount">How many rows match in total.</param>
    public PagedResult(IReadOnlyList<T> items, int page, int pageSize, int totalCount)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(totalCount);

        Items = items;
        Page = page;
        PageSize = pageSize;
        TotalCount = totalCount;
    }

    /// <summary>The rows on this page.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>The 1-based page number.</summary>
    public int Page { get; }

    /// <summary>How many rows a full page holds.</summary>
    public int PageSize { get; }

    /// <summary>How many rows match in total, across every page.</summary>
    public int TotalCount { get; }

    /// <summary>How many pages the matching rows fill, at least one.</summary>
    public int TotalPages => TotalCount == 0 ? 1 : (TotalCount + PageSize - 1) / PageSize;

    /// <summary>Whether a previous page exists.</summary>
    public bool HasPrevious => Page > 1;

    /// <summary>Whether a further page exists.</summary>
    public bool HasNext => Page < TotalPages;

    /// <summary>The 1-based index of the first row shown, or 0 when none is.</summary>
    public int FirstRowNumber => TotalCount == 0 ? 0 : ((Page - 1) * PageSize) + 1;

    /// <summary>The 1-based index of the last row shown, or 0 when none is.</summary>
    public int LastRowNumber => TotalCount == 0 ? 0 : FirstRowNumber + Items.Count - 1;
}

/// <summary>
/// The limits every paged list shares.
/// </summary>
/// <remarks>
/// Separate from <see cref="PagedResult{T}"/> because these do not vary with
/// the row type, and a static member on a generic type has to be reached
/// through some arbitrary instantiation of it (CA1000).
/// </remarks>
public static class Paging
{
    /// <summary>The largest page any caller may ask for.</summary>
    /// <remarks>
    /// A ceiling as well as a default: the page size arrives from a query
    /// string, and without one a request for ten million rows is a denial of
    /// service needing no more authority than being signed in.
    /// </remarks>
    public const int MaximumPageSize = 200;

    /// <summary>The page size used when none was asked for.</summary>
    public const int DefaultPageSize = 25;

    /// <summary>Clamps a requested page size into what the procedures accept.</summary>
    /// <remarks>
    /// Clamped rather than refused: a page size is a preference, and a silly one
    /// should give somebody the biggest sensible page rather than an error page.
    /// </remarks>
    public static int ClampPageSize(int? requested) =>
        requested is null or < 1 ? DefaultPageSize : Math.Min(requested.Value, MaximumPageSize);

    /// <summary>Clamps a requested page number to at least one.</summary>
    public static int ClampPage(int? requested) => requested is null or < 1 ? 1 : requested.Value;
}
