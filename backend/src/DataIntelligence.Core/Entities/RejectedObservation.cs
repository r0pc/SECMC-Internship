using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Entities;

/// <summary>
/// An observation the collector parsed but could not store. Keeps bad data out of the fact
/// table while preserving the evidence — a rejection spike is the earliest signal that a
/// publisher changed its payload shape.
/// </summary>
public class RejectedObservation
{
    public long RejectedObservationId { get; set; }
    public long CollectionRunId { get; set; }
    public string? SeriesCode { get; set; }

    /// <summary>The period as published. Often it is the reason the row was rejected.</summary>
    public string? ReferenceDateText { get; set; }

    public DateTime RejectedAtUtc { get; set; }
    public RejectionReason Reason { get; set; }
    public string? ReasonDetail { get; set; }
    public string? RawFragment { get; set; }

    public CollectionRun? Run { get; set; }
}
