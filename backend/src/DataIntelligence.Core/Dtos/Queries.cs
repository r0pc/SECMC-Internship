using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Dtos;

/// <summary>Filters for the series catalogue (FR-11).</summary>
public sealed record SeriesQuery
{
    public byte? DataSourceId { get; init; }
    public int? CategoryId { get; init; }
    public SeriesFrequency? Frequency { get; init; }
    public SeasonalAdjustment? SeasonalAdjustment { get; init; }

    /// <summary>Null returns active and inactive alike; the endpoint defaults it to true.</summary>
    public bool? IsActive { get; init; }

    /// <summary>Case-insensitive substring of the title or the series code.</summary>
    public string? Search { get; init; }

    /// <summary>
    /// Include each series' newest value. One extra query per distinct frequency in the page,
    /// so it is opt-out for callers that only need names.
    /// </summary>
    public bool IncludeLatest { get; init; } = true;

    public PageRequest Page { get; init; } = PageRequest.Normalize(null, null);
}

/// <summary>
/// A window onto one series' observations.
/// </summary>
/// <remarks>
/// The defaults are the ones a chart wants: current vintages only, in the series' own period
/// length, oldest first. <see cref="IncludeRevisions"/> and <see cref="AsOfUtc"/> open up the
/// history the append-only table retains (FR-4) for anyone who needs it.
/// </remarks>
public sealed record ObservationQuery
{
    public required int SeriesId { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }

    /// <summary>
    /// Defaults to the series' native period, which is what keeps a BLS annual-average row out
    /// of a monthly chart. Set explicitly to read those rows instead.
    /// </summary>
    public PeriodType? PeriodType { get; init; }

    /// <summary>
    /// Return superseded vintages alongside current ones. Off by default: a chart that plots
    /// every vintage draws a period once per revision.
    /// </summary>
    public bool IncludeRevisions { get; init; }

    /// <summary>
    /// Point-in-time read: the values this platform held at that instant, ignoring anything
    /// learned since. Answers "what did we believe June's CPI was, on 15 July?" — the question
    /// the append-only design exists to support (FR-4, FR-6).
    /// </summary>
    public DateTime? AsOfUtc { get; init; }

    public SortDirection Sort { get; init; } = SortDirection.Ascending;

    public PageRequest Page { get; init; } =
        PageRequest.Normalize(null, null, PageRequest.ObservationPageSizeLimit);
}

/// <summary>Request for one or more trend lines over a shared range (FR-10, FR-11).</summary>
public sealed record TrendQuery
{
    /// <summary>
    /// Capped by the endpoint. Each line is one indexed range scan, and a chart carrying more
    /// than a handful of series is unreadable long before it is slow.
    /// </summary>
    public required IReadOnlyList<int> SeriesIds { get; init; }

    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public TrendGranularity Granularity { get; init; } = TrendGranularity.Auto;
}

/// <summary>Filters for the collection-run log (FR-2).</summary>
public sealed record CollectionRunQuery
{
    public byte? DataSourceId { get; init; }
    public CollectionRunStatus? Status { get; init; }

    /// <summary>Filters on <c>StartedAtUtc</c>, which is what the log is indexed by.</summary>
    public DateTime? FromUtc { get; init; }

    public DateTime? ToUtc { get; init; }

    /// <summary>Failed and partial runs only — the operations panel's default view.</summary>
    public bool FailuresOnly { get; init; }

    public PageRequest Page { get; init; } = PageRequest.Normalize(null, null);
}
