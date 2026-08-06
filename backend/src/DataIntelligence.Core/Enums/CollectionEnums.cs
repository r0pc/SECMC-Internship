namespace DataIntelligence.Core.Enums;

/// <summary>Outcome of a single collection attempt (FR-2).</summary>
/// <remarks>
/// Names match the values allowed by the schema's CHECK constraints and are persisted as
/// strings, so renaming a member is a breaking schema change.
/// </remarks>
public enum CollectionRunStatus
{
    Running,
    Succeeded,
    /// <summary>Data was stored, but some observations were rejected during validation.</summary>
    PartialSuccess,
    Failed,
    /// <summary>Not attempted — source disabled, or robots.txt disallowed an HTML source.</summary>
    Skipped
}

public enum CollectionTriggerType
{
    Scheduled,
    Manual,
    Retry,
    Backfill
}

/// <summary>Why a run failed, recorded so the scheduler can log and alert without crashing.</summary>
public enum CollectionFailureCategory
{
    /// <summary>DNS failure, connection refused, network unreachable.</summary>
    Unreachable,
    Timeout,
    /// <summary>Reached the publisher but it returned a non-success status code.</summary>
    HttpError,
    /// <summary>
    /// The publisher refused because we have asked too often. Distinct from HttpError because
    /// the fix is a smaller query budget, not a retry — BLS caps unregistered callers hard.
    /// </summary>
    RateLimited,
    /// <summary>The response could not be read as JSON at all.</summary>
    ParseError,
    /// <summary>
    /// Valid JSON, but not the shape this adapter expects. For an API this is the equivalent
    /// of a layout change: the publisher altered its contract.
    /// </summary>
    SchemaChanged,
    /// <summary>Observations were extracted but none survived validation.</summary>
    Validation,
    /// <summary>Extraction succeeded; writing to SQL Server did not.</summary>
    Persistence,
    Unknown
}

/// <summary>Why a parsed observation was not stored.</summary>
public enum RejectionReason
{
    MissingField,
    TypeMismatch,
    OutOfRange,
    /// <summary>
    /// The publisher returned a series or rate this platform does not store — a BLS series other
    /// than CUUR0000SA0, or a rate other than SOFR. Logged rather than dropped so the exclusion
    /// is visible in the data instead of invisible in a filter. Rare in normal operation: both
    /// endpoints are scoped to what we ask for, so this appearing means the contract moved.
    /// </summary>
    UnknownSeries,
    /// <summary>Two records in one payload claimed the same period.</summary>
    DuplicatePeriod,
    /// <summary>The period token could not be turned into a reference date.</summary>
    UnparseablePeriod,
    SchemaDrift,
    Unknown
}

/// <summary>
/// The length of the period a CPI figure describes.
/// </summary>
/// <remarks>
/// Persisted to <c>core.CpiObservation.PeriodType</c> as a string, so renaming a member is a
/// breaking schema change. Not derivable from the series' frequency: BLS publishes M13 (the
/// annual average) and S01/S02 (the halves) in the same monthly response, and those are averages
/// <em>of</em> the monthly figures — charting or aggregating them together counts the year twice
/// over. SOFR has no equivalent: every row is one business day, so its table carries no such
/// column.
/// </remarks>
public enum PeriodType
{
    Month,
    Semiannual,
    Annual
}

/// <summary>
/// How often the publisher releases a series. Presentation metadata on the catalogue rather than
/// a stored column — each dataset's table has exactly one frequency by construction.
/// </summary>
public enum SeriesFrequency
{
    BusinessDaily,
    Daily,
    Weekly,
    Monthly,
    Quarterly,
    Semiannual,
    Annual
}

/// <summary>
/// Whether a series has been seasonally adjusted. Kept explicit because comparing an adjusted
/// series against an unadjusted one is a common and silent analytical error.
/// </summary>
public enum SeasonalAdjustment
{
    SeasonallyAdjusted,
    NotSeasonallyAdjusted,
    NotApplicable
}

/// <summary>The two datasets the platform stores, one table each.</summary>
public enum Dataset
{
    /// <summary>BLS series CUUR0000SA0 — <c>core.CpiObservation</c>.</summary>
    Cpi,

    /// <summary>The Secured Overnight Financing Rate — <c>core.SofrDailyRate</c>.</summary>
    Sofr
}

/// <summary>
/// Which column of a <c>core.SofrDailyRate</c> row a chartable series reads.
/// </summary>
/// <remarks>
/// A day's six measures are columns on one row, not six rows. Charting still wants them
/// individually, so the read side names the measure and projects the column.
/// </remarks>
public enum SofrMeasure
{
    Rate,
    Percentile1,
    Percentile25,
    Percentile75,
    Percentile99,
    Volume
}

/// <summary>How a source is retrieved.</summary>
public enum SourceAccessMethod
{
    /// <summary>An official JSON API. Both confirmed sources use this (SOW 9).</summary>
    RestApi,
    /// <summary>Retained so a scraped fallback importer stays modelled; not implemented.</summary>
    Html,
    Csv
}
