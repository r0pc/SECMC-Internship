using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Entities;

/// <summary>
/// One published figure for BLS series <see cref="SeriesCodeValue"/>, and nothing else. Append-only —
/// a revision inserts a new row and clears <see cref="IsCurrent"/> on the previous one (FR-4).
/// </summary>
/// <remarks>
/// One row per (year, period). BLS publishes more than twelve figures a year for this series:
/// twelve monthly index levels, an annual average, and two semiannual averages — the Annual,
/// HALF1 and HALF2 columns of the published CSV. All are stored, and
/// <see cref="PeriodType"/> is what keeps them apart. The annual and semiannual figures are
/// averages <em>of</em> the monthly ones, so anything that aggregated a year's rows without
/// filtering would count the same numbers three times.
/// <para>
/// Two independent dates, both required. <see cref="ReferenceDate"/> is the period the number
/// describes; <see cref="CollectedAtPkt"/> is when this platform learned it (FR-6). Answering
/// "what did we believe CPI for June was, on 15 July?" needs both.
/// </para>
/// </remarks>
public class CpiObservation
{
    /// <summary>
    /// The one BLS series in scope: CPI-U, all items, U.S. city average, not seasonally adjusted.
    /// Pinned by a CHECK constraint rather than left as a convention.
    /// </summary>
    public const string SeriesCodeValue = "CUUR0000SA0";

    public long CpiObservationId { get; set; }

    /// <summary>
    /// Always <see cref="SeriesCodeValue"/>. Carried on the row so an extract is self-describing;
    /// a second series would get its own table rather than a second value here.
    /// </summary>
    public string SeriesCode { get; set; } = SeriesCodeValue;

    /// <summary>
    /// First day of the period: 2026-06-01 for M06, 2026-01-01 for M13 and S01, 2026-07-01 for S02.
    /// </summary>
    public DateOnly ReferenceDate { get; set; }

    /// <summary>The calendar year — the CSV's Year column, and part of the natural key.</summary>
    public short ReferenceYear { get; set; }

    /// <summary>
    /// The publisher's own period token, verbatim: <c>M01</c>..<c>M12</c>, <c>M13</c> for the
    /// annual average, <c>S01</c>/<c>S02</c> for the halves.
    /// </summary>
    public string PeriodCode { get; set; } = string.Empty;

    public PeriodType PeriodType { get; set; }

    /// <summary>The index level as published, base 1982-84 = 100. Never rescaled or rounded.</summary>
    public decimal IndexValue { get; set; }

    /// <summary>
    /// BLS footnote codes, verbatim and uninterpreted. "R" is the publisher saying the figure was
    /// revised, which is exactly what makes a new vintage.
    /// </summary>
    public string? Footnotes { get; set; }

    /// <summary>0 is the first value seen for this period; each correction increments.</summary>
    public short RevisionNumber { get; set; }

    public bool IsCurrent { get; set; } = true;
    public DateTime? SupersededAtPkt { get; set; }

    public long CollectionRunId { get; set; }
    public DateTime CollectedAtPkt { get; set; }

    /// <summary>
    /// SHA-256 over the value tuple. Equal to the current vintage's hash means BLS reissued the
    /// same number, so no row is written (FR-3).
    /// </summary>
    public byte[] RowHash { get; set; } = [];

    public CollectionRun? Run { get; set; }
}
