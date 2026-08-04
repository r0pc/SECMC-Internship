using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Interfaces;

/// <summary>
/// Executes one complete collection cycle for one source: fetch, parse, validate, deduplicate,
/// apply revisions, persist, and record the outcome. The Worker owns <em>when</em> this runs.
/// </summary>
public interface ICollectionRunner
{
    /// <summary>
    /// Runs the cycle scheduled for <paramref name="scheduledForUtc"/> against one source.
    /// </summary>
    /// <remarks>
    /// Does not throw for collection failures — they are recorded on the run and returned, so a
    /// bad cycle can never take the scheduler down (FR-2). Only cancellation propagates.
    /// </remarks>
    Task<CollectionSummary> RunAsync(
        string sourceCode,
        DateTime scheduledForUtc,
        CollectionTriggerType trigger,
        CancellationToken cancellationToken);
}

/// <summary>What a cycle did, for logging and for the Worker's retry decision.</summary>
public sealed record CollectionSummary(
    string SourceCode,
    long? CollectionRunId,
    CollectionRunStatus Status,
    int Fetched,
    int Inserted,
    int Revised,
    int Unchanged,
    int Rejected,
    CollectionFailureCategory? FailureCategory,
    string? ErrorMessage)
{
    /// <summary>
    /// Only conditions another attempt could plausibly fix. A schema change or a validation
    /// failure will fail identically next time, and rate limiting needs a smaller budget rather
    /// than a faster retry.
    /// </summary>
    public bool ShouldRetry => Status == CollectionRunStatus.Failed
        && FailureCategory is CollectionFailureCategory.Unreachable
            or CollectionFailureCategory.Timeout
            or CollectionFailureCategory.HttpError;
}
