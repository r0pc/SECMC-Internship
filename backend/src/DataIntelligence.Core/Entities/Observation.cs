using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Entities;

/// <summary>
/// The fact table: one row per (series, reference period, vintage). Append-only — a revision
/// inserts a new row and clears <see cref="IsCurrent"/> on the previous one (FR-4).
/// </summary>
/// <remarks>
/// Two independent dates, both required. <see cref="ReferenceDate"/> is the period the number
/// describes; <see cref="CollectedAtUtc"/> is when this platform learned it (FR-6). Answering
/// "what did we believe CPI for June was, on 15 July?" needs both.
/// </remarks>
public class Observation
{
    public long ObservationId { get; set; }
    public int SeriesId { get; set; }

    /// <summary>Period start: 2026-06-01 for CPI "M06", 2026-07-31 for a SOFR business day.</summary>
    public DateOnly ReferenceDate { get; set; }

    public PeriodType PeriodType { get; set; }

    /// <summary>The publisher's own period token, verbatim: <c>M06</c>, <c>M13</c>.</summary>
    public string? SourcePeriodCode { get; set; }

    /// <summary>0 is the first value seen for this period; each correction increments.</summary>
    public short RevisionNumber { get; set; }

    public bool IsCurrent { get; set; } = true;
    public DateTime? SupersededAtUtc { get; set; }

    /// <summary>
    /// Wide enough that a series published in dollars rather than billions cannot overflow, and
    /// precise enough for the SOFR Index, which publishes eight decimals.
    /// </summary>
    public decimal Value { get; set; }

    /// <summary>
    /// Publisher annotation, verbatim: BLS footnote codes, or the NY Fed's revisionIndicator.
    /// Kept as published rather than interpreted.
    /// </summary>
    public string? SourceAnnotation { get; set; }

    public long CollectionRunId { get; set; }
    public DateTime CollectedAtUtc { get; set; }

    /// <summary>Computed and persisted by SQL Server as yyyyMMdd; never set in code.</summary>
    public int ReferenceDateKey { get; private set; }

    /// <summary>
    /// SHA-256 over the value tuple. Equal to the current vintage's hash means the publisher
    /// reissued the same number, so no row is written (FR-3).
    /// </summary>
    public byte[] RowHash { get; set; } = [];

    public Series? Series { get; set; }
    public CollectionRun? Run { get; set; }
}
