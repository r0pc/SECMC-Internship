using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Entities;

/// <summary>
/// One row per collection attempt (FR-1). Every attempt is recorded — including the ones
/// that fail — so the scheduler logs failures rather than crashing on them (FR-2), and so
/// the rolling 30-day success rate is answerable from the database (NFR Reliability).
/// </summary>
public class CollectionRun
{
    public long CollectionRunId { get; set; }

    /// <summary>
    /// The scheduled hour this run satisfies, truncated to the minute. Together with
    /// <see cref="Attempt"/> this is the run's idempotency key: a retry of the 10:00 cycle
    /// is (10:00, 2), which keeps retries distinguishable from a fresh cycle.
    /// </summary>
    public DateTime ScheduledForUtc { get; set; }

    public byte Attempt { get; set; } = 1;
    public CollectionTriggerType TriggerType { get; set; } = CollectionTriggerType.Scheduled;
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Computed by SQL Server from the start/complete timestamps; never set in code.</summary>
    public long? DurationMs { get; private set; }

    public CollectionRunStatus Status { get; set; } = CollectionRunStatus.Running;
    public string RequestUrl { get; set; } = string.Empty;
    public short? HttpStatusCode { get; set; }

    public int RecordsFetched { get; set; }
    public int RecordsInserted { get; set; }
    public int RecordsUnchanged { get; set; }
    public int RecordsRejected { get; set; }

    public CollectionFailureCategory? FailureCategory { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorDetail { get; set; }

    /// <summary>Set once a failure alert has been raised, so it is not raised twice.</summary>
    public DateTime? AlertSentAtUtc { get; set; }

    public ICollection<RawPayload> RawPayloads { get; set; } = [];
    public ICollection<RejectedRecord> RejectedRecords { get; set; } = [];
}
