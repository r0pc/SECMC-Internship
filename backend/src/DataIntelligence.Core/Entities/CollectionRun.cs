using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Entities;

/// <summary>
/// One row per collection attempt, per source (FR-1). Every attempt is recorded — including the
/// ones that fail — so the scheduler logs failures rather than crashing on them (FR-2), and the
/// rolling 30-day success rate stays answerable in SQL (NFR Reliability).
/// </summary>
public class CollectionRun
{
    public long CollectionRunId { get; set; }
    public byte DataSourceId { get; set; }

    /// <summary>
    /// The scheduled cycle this run satisfies. With <see cref="Attempt"/> this is the run's
    /// idempotency key, scoped per source so the two publishers can share a cycle time.
    /// </summary>
    public DateTime ScheduledForPkt { get; set; }

    public byte Attempt { get; set; } = 1;
    public CollectionTriggerType TriggerType { get; set; } = CollectionTriggerType.Scheduled;
    public DateTime StartedAtPkt { get; set; }
    public DateTime? CompletedAtPkt { get; set; }

    /// <summary>Computed by SQL Server from the two timestamps; never set in code.</summary>
    public long? DurationMs { get; private set; }

    public CollectionRunStatus Status { get; set; } = CollectionRunStatus.Running;
    public string RequestUrl { get; set; } = string.Empty;
    public short? HttpStatusCode { get; set; }

    public int ObservationsFetched { get; set; }
    public int ObservationsInserted { get; set; }

    /// <summary>Counted separately from inserts: a revision means a published figure moved.</summary>
    public int ObservationsRevised { get; set; }

    public int ObservationsUnchanged { get; set; }
    public int ObservationsRejected { get; set; }

    public CollectionFailureCategory? FailureCategory { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorDetail { get; set; }
    public DateTime? AlertSentAtPkt { get; set; }

    public DataSource? DataSource { get; set; }
    public ICollection<RawPayload> RawPayloads { get; set; } = [];
    public ICollection<RejectedObservation> RejectedObservations { get; set; } = [];
}
