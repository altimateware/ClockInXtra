namespace Attendance.Admin.Models;

/// <summary>
/// What the shared pager partial needs to draw itself.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately carries the <b>route values of the current page</b> rather than
/// building links from the query string. A pager that reconstructs the URL
/// loses whatever filter the administrator had applied, so page two of a search
/// quietly becomes page two of everything — which, on a page whose buttons
/// revoke devices and deactivate employees, is worth more care than a paging
/// control usually gets.
/// </para>
/// </remarks>
public sealed class PagerViewModel
{
    /// <summary>The 1-based page shown.</summary>
    public required int Page { get; init; }

    /// <summary>How many pages there are, at least one.</summary>
    public required int TotalPages { get; init; }

    /// <summary>How many rows match in total.</summary>
    public required int TotalCount { get; init; }

    /// <summary>The page size in force, so the links can preserve it.</summary>
    public required int PageSize { get; init; }

    /// <summary>The 1-based index of the first row shown, or 0.</summary>
    public required int FirstRowNumber { get; init; }

    /// <summary>The 1-based index of the last row shown, or 0.</summary>
    public required int LastRowNumber { get; init; }

    /// <summary>What the rows are, for the count sentence: "employees", "devices".</summary>
    public required string Noun { get; init; }

    /// <summary>The action the links point at.</summary>
    public required string Action { get; init; }

    /// <summary>
    /// Everything else in the query string, carried through every link so a
    /// filter survives paging.
    /// </summary>
    public IReadOnlyDictionary<string, string?> RouteValues { get; init; } =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>Whether a previous page exists.</summary>
    public bool HasPrevious => Page > 1;

    /// <summary>Whether a further page exists.</summary>
    public bool HasNext => Page < TotalPages;

    /// <summary>The route values for a given page, filters included.</summary>
    /// <remarks>
    /// Carries the page size as well as the filters. Found in the browser, not
    /// by a test: with a page size of five, "Next" linked to
    /// <c>?page=3</c> and landed on a page of twenty-five, so the rows an
    /// administrator was looking at were not the rows they got. The test that
    /// was meant to cover this only checked the search box kept its text.
    ///
    /// Omitted when it is the default, to keep ordinary links clean.
    /// </remarks>
    public Dictionary<string, string?> RouteValuesFor(int page)
    {
        Dictionary<string, string?> values = new(RouteValues, StringComparer.Ordinal)
        {
            ["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        if (PageSize != Attendance.Application.Abstractions.Paging.DefaultPageSize)
        {
            values["pageSize"] = PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return values;
    }
}
