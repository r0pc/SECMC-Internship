using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Interfaces;

/// <summary>
/// Executes one complete collection cycle: robots check, fetch, parse, validate, deduplicate,
/// persist, and record the outcome. The Worker owns *when* this runs; this owns *what* happens.
/// </summary>
public interface ICollectionRunner
{
    /// <summary>
    /// Runs the cycle scheduled for <paramref name="scheduledForUtc"/>.
    /// </summary>
    /// <remarks>
    /// Does not throw for collection failures — they are recorded on the run and returned, so a
    /// bad cycle can never take the scheduler down (FR-2). Only cancellation propagates.
    /// </remarks>
    Task<CollectionSummary> RunAsync(
        DateTime scheduledForUtc,
        CollectionTriggerType trigger,
        CancellationToken cancellationToken);
}

/// <summary>What a cycle did, for logging and for the Worker's retry decision.</summary>
public sealed record CollectionSummary(
    long? CollectionRunId,
    CollectionRunStatus Status,
    int RecordsFetched,
    int RecordsInserted,
    int RecordsUnchanged,
    int RecordsRejected,
    CollectionFailureCategory? FailureCategory,
    string? ErrorMessage)
{
    public bool ShouldRetry => Status == CollectionRunStatus.Failed
        && FailureCategory is CollectionFailureCategory.Unreachable
            or CollectionFailureCategory.Timeout
            or CollectionFailureCategory.HttpError;
}
