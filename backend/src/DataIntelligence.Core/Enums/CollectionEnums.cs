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
    /// <summary>The publisher returned a series this platform does not track.</summary>
    UnknownSeries,
    /// <summary>Two records in one payload claimed the same series and period.</summary>
    DuplicatePeriod,
    /// <summary>The period token could not be turned into a reference date.</summary>
    UnparseablePeriod,
    SchemaDrift,
    Unknown
}

/// <summary>
/// The length of the period an observation describes.
/// </summary>
/// <remarks>
/// Not derivable from the series' frequency: a BLS monthly series also publishes M13 (annual
/// average) and S01/S02 (semiannual) rows in the same response, and averaging those into a
/// monthly trend would double-count the year.
/// </remarks>
public enum PeriodType
{
    Day,
    Week,
    Month,
    Quarter,
    Semiannual,
    Annual
}

/// <summary>How often the publisher releases a series.</summary>
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

/// <summary>How a source is retrieved.</summary>
public enum SourceAccessMethod
{
    /// <summary>An official JSON API. Both confirmed sources use this (SOW 9).</summary>
    RestApi,
    /// <summary>Retained so a scraped fallback importer stays modelled; not implemented.</summary>
    Html,
    Csv
}
