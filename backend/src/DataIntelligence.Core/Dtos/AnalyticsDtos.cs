using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Dtos;

/// <summary>
/// One stored observation (FR-4, FR-6).
/// </summary>
/// <remarks>
/// Both dates matter and neither substitutes for the other: <see cref="ReferenceDate"/> is the
/// period the number describes, <see cref="CollectedAtUtc"/> is when this platform learned it.
/// A revision keeps the reference date and gets a new collection timestamp.
/// </remarks>
public sealed record ObservationDto
{
    public required long ObservationId { get; init; }
    public required int SeriesId { get; init; }
    public required DateOnly ReferenceDate { get; init; }
    public required PeriodType PeriodType { get; init; }

    /// <summary>The publisher's own period token, verbatim: <c>M06</c>, <c>M13</c>.</summary>
    public string? SourcePeriodCode { get; init; }

    public required decimal Value { get; init; }

    /// <summary>0 is the first value seen for this period; each correction increments.</summary>
    public required short RevisionNumber { get; init; }

    /// <summary>False for a superseded vintage — only returned when revisions are requested.</summary>
    public required bool IsCurrent { get; init; }

    public DateTime? SupersededAtUtc { get; init; }

    /// <summary>Publisher annotation as published: BLS footnote codes, the NY Fed revision indicator.</summary>
    public string? SourceAnnotation { get; init; }

    public required DateTime CollectedAtUtc { get; init; }
    public required long CollectionRunId { get; init; }
}

/// <summary>A trend line: one series' points over the requested range (FR-10).</summary>
public sealed record TrendSeriesDto
{
    public required int SeriesId { get; init; }
    public required string SeriesCode { get; init; }
    public required string Title { get; init; }

    /// <summary>Axis label. Series with different units must not share an axis.</summary>
    public required string Unit { get; init; }

    public byte? DecimalPlaces { get; init; }

    /// <summary>The bucket width actually used, after <c>Auto</c> was resolved.</summary>
    public required TrendGranularity Granularity { get; init; }

    public required IReadOnlyList<TrendPointDto> Points { get; init; }
}

/// <summary>
/// One point on a trend line.
/// </summary>
/// <remarks>
/// When the bucket holds a single observation — the common case, a monthly series charted by
/// month — <see cref="Value"/>, <see cref="Minimum"/> and <see cref="Maximum"/> are that one
/// number and <see cref="ObservationCount"/> is 1. When a bucket aggregates (business-daily SOFR
/// charted by month), <see cref="Value"/> is the mean and the min/max carry the spread, so a
/// chart can draw a band rather than implying the average was the whole story.
/// </remarks>
public sealed record TrendPointDto
{
    public required DateOnly BucketStart { get; init; }

    /// <summary>Inclusive. Equal to <see cref="BucketStart"/> for unbucketed points.</summary>
    public required DateOnly BucketEnd { get; init; }

    /// <summary>Mean of the observations in the bucket.</summary>
    public required decimal Value { get; init; }

    public required decimal Minimum { get; init; }
    public required decimal Maximum { get; init; }
    public required int ObservationCount { get; init; }
}

/// <summary>
/// Headline numbers for one series (FR-10): where it stands now and how that compares.
/// </summary>
public sealed record SeriesKpiDto
{
    public required int SeriesId { get; init; }
    public required string SeriesCode { get; init; }
    public required string Title { get; init; }
    public required string Unit { get; init; }
    public byte? DecimalPlaces { get; init; }
    public required SeriesFrequency Frequency { get; init; }
    public required SeasonalAdjustment SeasonalAdjustment { get; init; }

    /// <summary>Null when nothing has been collected for this series yet.</summary>
    public SeriesLatestPointDto? Latest { get; init; }

    /// <summary>The release before <see cref="Latest"/>.</summary>
    public decimal? PreviousValue { get; init; }

    public DateOnly? PreviousReferenceDate { get; init; }

    /// <summary>Latest minus previous, in the series' own unit.</summary>
    public decimal? ChangeFromPrevious { get; init; }

    /// <summary>Null when the previous value was zero.</summary>
    public decimal? PercentChangeFromPrevious { get; init; }

    /// <summary>
    /// The most recent release at or before one year prior. Not an exact-date match: SOFR does
    /// not publish on weekends, so an exact lookup would return nothing roughly two days in seven.
    /// </summary>
    public decimal? YearAgoValue { get; init; }

    public DateOnly? YearAgoReferenceDate { get; init; }
    public decimal? ChangeFromYearAgo { get; init; }

    /// <summary>
    /// Year-over-year percentage change — for CPI, the inflation rate as normally quoted.
    /// </summary>
    public decimal? PercentChangeFromYearAgo { get; init; }
}

/// <summary>One collection attempt, for the operations panel (FR-2).</summary>
public sealed record CollectionRunDto
{
    public required long CollectionRunId { get; init; }
    public required byte DataSourceId { get; init; }
    public required string SourceCode { get; init; }
    public required DateTime ScheduledForUtc { get; init; }
    public required byte Attempt { get; init; }
    public required CollectionTriggerType TriggerType { get; init; }
    public required DateTime StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public long? DurationMs { get; init; }
    public required CollectionRunStatus Status { get; init; }
    public short? HttpStatusCode { get; init; }
    public required int ObservationsFetched { get; init; }
    public required int ObservationsInserted { get; init; }

    /// <summary>Counted apart from inserts: a revision means a published figure moved.</summary>
    public required int ObservationsRevised { get; init; }

    public required int ObservationsUnchanged { get; init; }
    public required int ObservationsRejected { get; init; }
    public CollectionFailureCategory? FailureCategory { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Collection health for one source over a rolling window (NFR Reliability — the ≥99% target).
/// </summary>
public sealed record SourceHealthDto
{
    public required byte DataSourceId { get; init; }
    public required string SourceCode { get; init; }
    public required string Name { get; init; }
    public required bool IsEnabled { get; init; }
    public required int WindowDays { get; init; }
    public required int TotalRuns { get; init; }
    public required int SucceededRuns { get; init; }
    public required int PartialRuns { get; init; }
    public required int FailedRuns { get; init; }

    /// <summary>
    /// Succeeded plus partial, over total, as a percentage. Null when the window holds no runs —
    /// which is itself worth showing, since "no data" and "100%" are very different states.
    /// </summary>
    public decimal? SuccessRatePercent { get; init; }

    public DateTime? LastRunAtUtc { get; init; }
    public DateTime? LastSuccessAtUtc { get; init; }
    public CollectionRunStatus? LastRunStatus { get; init; }
    public CollectionFailureCategory? LastFailureCategory { get; init; }
    public string? LastErrorMessage { get; init; }

    /// <summary>
    /// Consecutive failures ending at the most recent run. Non-zero means collection is currently
    /// broken for this source rather than merely having been broken at some point in the window.
    /// </summary>
    public required int ConsecutiveFailures { get; init; }
}

/// <summary>Everything the dashboard landing page needs, in one request (FR-10).</summary>
public sealed record DashboardSummaryDto
{
    public required int SourceCount { get; init; }
    public required int ActiveSeriesCount { get; init; }
    public required int CategoryCount { get; init; }

    /// <summary>Current vintages only — superseded revisions are not double-counted.</summary>
    public required long ObservationCount { get; init; }

    public DateOnly? EarliestReferenceDate { get; init; }
    public DateOnly? LatestReferenceDate { get; init; }
    public DateTime? LastCollectionAtUtc { get; init; }
    public required IReadOnlyList<SourceHealthDto> Sources { get; init; }
}
