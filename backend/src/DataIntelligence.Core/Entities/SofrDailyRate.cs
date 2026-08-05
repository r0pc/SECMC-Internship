namespace DataIntelligence.Core.Entities;

/// <summary>
/// The Secured Overnight Financing Rate for one business day. Append-only, like
/// <see cref="CpiObservation"/>: a correction inserts a new vintage rather than overwriting (FR-4).
/// </summary>
/// <remarks>
/// One row per business day, rate type SOFR only. The NY Fed publishes five secured and unsecured
/// rates in the same payload — EFFR, OBFR, TGCR, BGCR and SOFR — and the other four are out of
/// scope: they are rejected on the way in, not stored, and <see cref="RateType"/> is pinned by a
/// CHECK constraint so one cannot land here by accident.
/// <para>
/// The six measures a day carries — the rate, four percentiles, and volume — are columns rather
/// than rows. They are one observation of one instrument on one day, and splitting them across
/// rows would mean five self-joins to draw the distribution band that is the whole point of
/// publishing them.
/// </para>
/// </remarks>
public class SofrDailyRate
{
    /// <summary>The one rate in scope. Pinned by a CHECK constraint.</summary>
    public const string RateTypeValue = "SOFR";

    public long SofrDailyRateId { get; set; }

    /// <summary>Always <see cref="RateTypeValue"/>.</summary>
    public string RateType { get; set; } = RateTypeValue;

    /// <summary>
    /// The business day the rate covers. Published the following business morning, ~08:00 ET,
    /// which is why this and <see cref="CollectedAtUtc"/> are never the same instant.
    /// </summary>
    public DateOnly EffectiveDate { get; set; }

    /// <summary>The overnight rate, percent per annum.</summary>
    public decimal RatePercent { get; set; }

    /// <summary>
    /// The rate distribution across the underlying trades. Nullable because the publisher omits
    /// them on low-volume days; a missing measure is not a broken record.
    /// </summary>
    public decimal? Percentile1Percent { get; set; }

    public decimal? Percentile25Percent { get; set; }
    public decimal? Percentile75Percent { get; set; }
    public decimal? Percentile99Percent { get; set; }

    /// <summary>
    /// Transaction volume in the unit published — billions of dollars. Never rescaled to dollars
    /// on the way in: a silent factor of a billion is not visible in the data afterwards.
    /// </summary>
    public decimal? VolumeUsdBillions { get; set; }

    /// <summary>
    /// The publisher's own compounded averages and index.
    /// </summary>
    /// <remarks>
    /// Modelled but not collected. They are published on the separate SOFR Averages and Index
    /// endpoint rather than on the daily rate record, and are empty in the CSV extract too. The
    /// columns exist so that adding that endpoint later is an adapter change rather than a
    /// migration, and so nothing is ever tempted to compute a "SOFR average" here and store it
    /// where the publisher's own compounded figure belongs.
    /// </remarks>
    public decimal? Average30DayPercent { get; set; }

    public decimal? Average90DayPercent { get; set; }
    public decimal? Average180DayPercent { get; set; }
    public decimal? SofrIndexValue { get; set; }

    /// <summary>
    /// The publisher's revision indicator, verbatim: "Y" when this day's rate has been corrected.
    /// Distinct from <see cref="RevisionNumber"/>, which counts how often <em>we</em> have seen it
    /// change — the publisher's statement and our observation of it are not the same fact.
    /// </summary>
    public string? RevisionIndicator { get; set; }

    public string? FootnoteId { get; set; }

    /// <summary>0 is the first value seen for this day; each correction increments.</summary>
    public short RevisionNumber { get; set; }

    public bool IsCurrent { get; set; } = true;
    public DateTime? SupersededAtUtc { get; set; }

    public long CollectionRunId { get; set; }
    public DateTime CollectedAtUtc { get; set; }

    /// <summary>SHA-256 over the measures, so an unchanged day writes no row (FR-3).</summary>
    public byte[] RowHash { get; set; } = [];

    public CollectionRun? Run { get; set; }
}
