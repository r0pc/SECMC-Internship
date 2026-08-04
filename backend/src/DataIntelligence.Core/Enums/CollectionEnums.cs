namespace DataIntelligence.Core.Enums;

/// <summary>Outcome of a single collection attempt (FR-2).</summary>
/// <remarks>
/// Names match the values allowed by <c>CK_CollectionRun_Status</c> in the schema;
/// they are persisted as strings, so renaming a member is a breaking schema change.
/// </remarks>
public enum CollectionRunStatus
{
    Running,
    Succeeded,
    /// <summary>Data was stored, but some records were rejected during validation.</summary>
    PartialSuccess,
    Failed,
    /// <summary>Not attempted — no source configured, source disabled, or robots.txt disallowed it.</summary>
    Skipped
}

/// <summary>What caused a run to start.</summary>
public enum CollectionTriggerType
{
    Scheduled,
    Manual,
    Retry,
    Backfill
}

/// <summary>
/// Why a run failed. Recorded so the scheduler can log and alert without crashing (FR-2),
/// and so <c>LayoutChanged</c> can be alerted on separately — it is the leading risk in SOW 9.
/// </summary>
public enum CollectionFailureCategory
{
    /// <summary>DNS failure, connection refused, network unreachable.</summary>
    Unreachable,
    Timeout,
    /// <summary>Reached the server but it returned a non-success status code.</summary>
    HttpError,
    /// <summary>The response could not be parsed as the expected document type.</summary>
    ParseError,
    /// <summary>Parsed fine, but the configured selectors matched nothing — the source's markup moved.</summary>
    LayoutChanged,
    /// <summary>Records were extracted but none survived validation.</summary>
    Validation,
    /// <summary>Extraction succeeded; writing to SQL Server did not.</summary>
    Persistence,
    Unknown
}

/// <summary>Why a parsed record was not promoted to a snapshot.</summary>
public enum RejectionReason
{
    MissingField,
    TypeMismatch,
    OutOfRange,
    /// <summary>Two records in the same payload claimed the same source key.</summary>
    DuplicateKey,
    SchemaDrift,
    Unknown
}

/// <summary>Storage type for an extension attribute.</summary>
public enum AttributeDataType
{
    Text,
    Number,
    Date,
    Boolean
}
