using DataIntelligence.Core.Analytics;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataIntelligence.Infrastructure.Analytics;

/// <summary>
/// Turns a catalogue entry into rows the read side can treat uniformly.
/// </summary>
/// <remarks>
/// The two datasets are separate tables with different columns, which is the point of the schema.
/// The dashboards, though, ask the same three questions of both — what is the latest value, how
/// has it moved, plot it over this range — so exactly one place translates a
/// <see cref="SeriesDefinition"/> into a comparable stream of (date, value) rows, and everything
/// downstream is written once.
/// <para>
/// This is deliberately a projection and not a shared base table. Storing the two datasets in one
/// table so the read side could be simple is what the previous design did, and it cost a nullable
/// column per measure and a join on every query. Paying for the difference here, once, is
/// cheaper than paying for it in the schema forever.
/// </para>
/// </remarks>
internal static class MeasureQueries
{
    /// <summary>
    /// Every stored vintage for one series, unfiltered by currency so the caller can ask for
    /// revision history or a point-in-time read.
    /// </summary>
    /// <param name="cpiPeriodType">
    /// Which CPI period length to read. Defaults to <see cref="PeriodType.Month"/>, which is what
    /// keeps a BLS annual average out of a monthly chart. Ignored for SOFR.
    /// </param>
    public static IQueryable<MeasureRow> Rows(
        DataIntelligenceDbContext db,
        SeriesDefinition definition,
        PeriodType? cpiPeriodType = null)
    {
        if (definition.Dataset == Dataset.Cpi)
        {
            var periodType = cpiPeriodType ?? PeriodType.Month;

            return db.CpiObservations.AsNoTracking()
                .Where(o => o.PeriodType == periodType)
                .Select(o => new MeasureRow
                {
                    ObservationId = o.CpiObservationId,
                    ReferenceDate = o.ReferenceDate,
                    PeriodType = o.PeriodType,
                    PeriodCode = o.PeriodCode,
                    Value = o.IndexValue,
                    RevisionNumber = o.RevisionNumber,
                    IsCurrent = o.IsCurrent,
                    SupersededAtPkt = o.SupersededAtPkt,
                    Annotation = o.Footnotes,
                    CollectedAtPkt = o.CollectedAtPkt,
                    CollectionRunId = o.CollectionRunId
                });
        }

        var measure = definition.Measure;

        // A CASE over columns of the same row, which is as cheap as reading one of them. Written
        // as a conditional chain rather than a switch because a switch statement does not
        // translate to SQL, and rebuilt in a second projection so the nullable measures can be
        // filtered before the value is dereferenced.
        return db.SofrDailyRates.AsNoTracking()
            .Select(r => new
            {
                r.SofrDailyRateId,
                r.EffectiveDate,
                r.RevisionNumber,
                r.IsCurrent,
                r.SupersededAtPkt,
                r.RevisionIndicator,
                r.CollectedAtPkt,
                r.CollectionRunId,
                Value =
                    measure == SofrMeasure.Percentile1 ? r.Percentile1Percent :
                    measure == SofrMeasure.Percentile25 ? r.Percentile25Percent :
                    measure == SofrMeasure.Percentile75 ? r.Percentile75Percent :
                    measure == SofrMeasure.Percentile99 ? r.Percentile99Percent :
                    measure == SofrMeasure.Volume ? r.VolumeUsdBillions :
                    r.RatePercent
            })
            // A percentile the publisher omitted on a low-volume day is absent, not zero, and a
            // chart must show a gap rather than a plunge to the axis.
            .Where(x => x.Value != null)
            .Select(x => new MeasureRow
            {
                ObservationId = x.SofrDailyRateId,
                ReferenceDate = x.EffectiveDate,
                PeriodType = null,
                PeriodCode = null,
                Value = x.Value!.Value,
                RevisionNumber = x.RevisionNumber,
                IsCurrent = x.IsCurrent,
                SupersededAtPkt = x.SupersededAtPkt,
                Annotation = x.RevisionIndicator,
                CollectedAtPkt = x.CollectedAtPkt,
                CollectionRunId = x.CollectionRunId
            });
    }

    /// <summary>The same measure mapping as <see cref="Rows"/>, for a row already loaded.</summary>
    public static decimal? Read(SofrDailyRate row, SofrMeasure? measure) => measure switch
    {
        SofrMeasure.Percentile1 => row.Percentile1Percent,
        SofrMeasure.Percentile25 => row.Percentile25Percent,
        SofrMeasure.Percentile75 => row.Percentile75Percent,
        SofrMeasure.Percentile99 => row.Percentile99Percent,
        SofrMeasure.Volume => row.VolumeUsdBillions,

        // The rate is the default because it is what everyone means by "SOFR", and the one
        // measure a stored row cannot be missing.
        _ => row.RatePercent
    };
}

/// <summary>
/// One stored value from either dataset, in the shape the dashboards read.
/// </summary>
/// <remarks>
/// Init-only properties rather than a positional record, and projected with an object
/// initialiser: EF Core can see through a member initialisation and translate a later
/// <c>Where</c> on <see cref="ReferenceDate"/> into SQL, but a constructor call is opaque to it
/// and the whole query would fall back to client evaluation — which for the full CPI history
/// means pulling every row across the wire to filter it in memory.
/// </remarks>
internal sealed record MeasureRow
{
    /// <summary>The row's key in its own table. Unique within a dataset, not across.</summary>
    public required long ObservationId { get; init; }

    public required DateOnly ReferenceDate { get; init; }

    /// <summary>CPI only; null for SOFR, whose table has no such column.</summary>
    public PeriodType? PeriodType { get; init; }

    /// <summary>CPI only: the publisher's period token, verbatim.</summary>
    public string? PeriodCode { get; init; }

    public required decimal Value { get; init; }
    public required short RevisionNumber { get; init; }
    public required bool IsCurrent { get; init; }
    public DateTime? SupersededAtPkt { get; init; }

    /// <summary>
    /// Whatever the publisher said about the row, verbatim: BLS footnote codes, or the NY Fed's
    /// revision indicator.
    /// </summary>
    public string? Annotation { get; init; }

    public required DateTime CollectedAtPkt { get; init; }
    public required long CollectionRunId { get; init; }
}
