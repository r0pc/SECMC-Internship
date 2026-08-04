using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Entities;

/// <summary>
/// A record the collector extracted but could not validate. Keeps bad data out of the fact
/// table while preserving the evidence — a sudden spike in rejections is the earliest signal
/// that the source's markup changed (SOW 9, Risk 1).
/// </summary>
public class RejectedRecord
{
    public long RejectedRecordId { get; set; }
    public long CollectionRunId { get; set; }
    public string? SourceKey { get; set; }
    public DateTime RejectedAtUtc { get; set; }
    public RejectionReason Reason { get; set; }
    public string? ReasonDetail { get; set; }

    /// <summary>The offending fragment, truncated. Enough to reproduce without storing the whole page.</summary>
    public string? RawFragment { get; set; }

    public CollectionRun? Run { get; set; }
}
