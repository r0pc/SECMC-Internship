namespace DataIntelligence.Core.Dtos;

/// <summary>
/// A normalised page request. Every list endpoint takes one, so paging behaves identically
/// across the API and the frontend can write one pager component (FR-7).
/// </summary>
/// <remarks>
/// Out-of-range input is clamped rather than rejected. A dashboard that asks for page 0 or for
/// ten thousand rows has a bug worth fixing, but failing the request turns that bug into a
/// blank screen; clamping keeps the page rendering while the response's echoed
/// <see cref="PagedResult{T}.PageSize"/> shows what was actually served.
/// </remarks>
public readonly record struct PageRequest
{
    public const int DefaultPageSize = 100;

    /// <summary>
    /// Ceiling for catalogue-style lists. The observations endpoint lowers it further — see
    /// <see cref="ObservationPageSizeLimit"/>.
    /// </summary>
    public const int MaxPageSize = 500;

    /// <summary>
    /// Ceiling for observation pages. Higher than the catalogue limit because a chart legitimately
    /// wants a year of business-daily data in one request (~260 points), and lower than unbounded
    /// because the 3-second dashboard budget (NFR Performance) is spent on serialisation long
    /// before SQL Server runs out of rows.
    /// </summary>
    public const int ObservationPageSizeLimit = 2000;

    private PageRequest(int page, int pageSize)
    {
        Page = page;
        PageSize = pageSize;
    }

    /// <summary>1-based, matching how a pager is labelled in the UI.</summary>
    public int Page { get; }

    public int PageSize { get; }

    public int Skip => (Page - 1) * PageSize;

    public static PageRequest Normalize(int? page, int? pageSize, int maxPageSize = MaxPageSize)
    {
        var normalizedPage = page is null or < 1 ? 1 : page.Value;

        var normalizedSize = pageSize switch
        {
            null or < 1 => Math.Min(DefaultPageSize, maxPageSize),
            _ => Math.Min(pageSize.Value, maxPageSize)
        };

        return new PageRequest(normalizedPage, normalizedSize);
    }
}

/// <summary>One page of results plus the metadata a pager needs to render itself.</summary>
public sealed record PagedResult<T>
{
    public required IReadOnlyList<T> Items { get; init; }

    public required int Page { get; init; }

    public required int PageSize { get; init; }

    /// <summary>Total matching rows, ignoring paging.</summary>
    public required int TotalCount { get; init; }

    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;

    public static PagedResult<T> From(IReadOnlyList<T> items, PageRequest page, int totalCount) =>
        new()
        {
            Items = items,
            Page = page.Page,
            PageSize = page.PageSize,
            TotalCount = totalCount
        };

    public static PagedResult<T> Empty(PageRequest page) => From([], page, 0);
}
