using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Dtos;

/// <summary>
/// One stored observation (FR-4, FR-6).
/// </summary>
/// <remarks>
/// Both dates matter and neither substitutes for the other: <see cref="ReferenceDate"/> is the
/// period the number describes, <see cref="CollectedAtPkt"/> is when this platform learned it.
/// A revision keeps the reference date and gets a new collection timestamp.
/// </remarks>
public sealed record ObservationDto
{
    public required long ObservationId { get; init; }
    public required string SeriesKey { get; init; }
    public required DateOnly ReferenceDate { get; init; }

    /// <summary>
    /// The period length, for CPI. Null for SOFR, where every row is one business day and the
    /// table carries no such column.
    /// </summary>
    public PeriodType? PeriodType { get; init; }

    /// <summary>
    /// The publisher's own period token, verbatim: <c>M06</c>, <c>M13</c>. Null for SOFR, whose
    /// period is the effective date itself.
    /// </summary>
    public string? PeriodCode { get; init; }

    public required decimal Value { get; init; }

    /// <summary>0 is the first value seen for this period; each correction increments.</summary>
    public required short RevisionNumber { get; init; }

    /// <summary>False for a superseded vintage — only returned when revisions are requested.</summary>
    public required bool IsCurrent { get; init; }

    public DateTime? SupersededAtPkt { get; init; }

    /// <summary>Publisher annotation as published: BLS footnote codes, the NY Fed revision indicator.</summary>
    public string? SourceAnnotation { get; init; }

    public required DateTime CollectedAtPkt { get; init; }
    public required long CollectionRunId { get; init; }
}

/// <summary>A trend line: one series' points over the requested range (FR-10).</summary>
public sealed record TrendSeriesDto
{
    public required string SeriesKey { get; init; }
    public required string Title { get; init; }

    /// <summary>Axis label. Series with different units must not share an axis.</summary>
    public required string Unit { get; init; }

    public required byte DecimalPlaces { get; init; }

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
    public required string SeriesKey { get; init; }
    public required string Title { get; init; }
    public required string Unit { get; init; }
    public required byte DecimalPlaces { get; init; }
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
    public required DateTime ScheduledForPkt { get; init; }
    public required byte Attempt { get; init; }
    public required CollectionTriggerType TriggerType { get; init; }
    public required DateTime StartedAtPkt { get; init; }
    public DateTime? CompletedAtPkt { get; init; }
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

    public DateTime? LastRunAtPkt { get; init; }
    public DateTime? LastSuccessAtPkt { get; init; }
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

    /// <summary>Chartable series, from the catalogue.</summary>
    public required int SeriesCount { get; init; }

    /// <summary>
    /// Stored rows per dataset, current vintages only — superseded revisions are not
    /// double-counted. Reported separately because they are separate tables measuring different
    /// things: a CPI row is a month, a SOFR row is a business day, and one total would invite
    /// the reader to compare them.
    /// </summary>
    public required long CpiObservationCount { get; init; }

    public required long SofrObservationCount { get; init; }

    /// <summary>CPI monthly figures only — the annual and semiannual rows are not a span of history.</summary>
    public DateOnly? EarliestCpiMonth { get; init; }

    public DateOnly? LatestCpiMonth { get; init; }
    public DateOnly? EarliestSofrDate { get; init; }
    public DateOnly? LatestSofrDate { get; init; }
    public DateTime? LastCollectionAtPkt { get; init; }
    public required IReadOnlyList<SourceHealthDto> Sources { get; init; }
}
