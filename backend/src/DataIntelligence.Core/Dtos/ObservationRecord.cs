using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Dtos;

/// <summary>
/// One row as extracted from a publisher's response, before validation and persistence. The
/// boundary between the source adapters (publisher-specific) and everything downstream.
/// </summary>
/// <remarks>
/// Abstract because the two datasets are genuinely different shapes, and pretending otherwise is
/// what the previous design got wrong: a SOFR business day is one record carrying six measures,
/// not six records carrying one each. Each subtype mirrors the table it is written to, so the
/// adapter, the validator, and the schema all describe the same thing.
/// </remarks>
public abstract record ObservationRecord
{
    /// <summary>
    /// Field delimiter for the row hash: U+001F UNIT SEPARATOR. Written as a cast rather than as
    /// a literal, because a literal control character is invisible in source and survives editing
    /// only by luck.
    /// </summary>
    protected const char FieldSeparator = (char)0x1F;

    /// <summary>
    /// Stands in for an absent value: U+001E RECORD SEPARATOR — a character no published value
    /// can contain, so "no annotation" cannot collide with a real one.
    /// </summary>
    protected const char NullMarker = (char)0x1E;

    /// <summary>The publisher's identifier for what this row measures, for rejection logging.</summary>
    public abstract string SeriesCode { get; }

    /// <summary>First day of the period the row describes.</summary>
    public abstract DateOnly ReferenceDate { get; init; }

    /// <summary>The period as published, for rejection logging: <c>2026/M06</c>, <c>2026-08-03</c>.</summary>
    public abstract string ReferenceLabel { get; }

    /// <summary>
    /// SHA-256 over the measured values, used to decide whether the publisher has actually
    /// changed anything since the last collection (FR-3).
    /// </summary>
    public abstract byte[] ComputeRowHash();

    /// <summary>
    /// Hashes a list of already-formatted fields.
    /// </summary>
    /// <remarks>
    /// <see cref="CultureInfo.InvariantCulture"/> is used by every caller, so a host's regional
    /// settings cannot change the hash of identical data and make a failover look like a mass
    /// revision.
    /// </remarks>
    protected static byte[] Hash(params string?[] fields)
    {
        var builder = new StringBuilder();

        foreach (var field in fields)
        {
            if (field is null)
            {
                builder.Append(NullMarker);
            }
            else
            {
                builder.Append(field);
            }

            builder.Append(FieldSeparator);
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    /// <summary>
    /// Renders a decimal for hashing.
    /// </summary>
    /// <remarks>
    /// "G29" normalises the scale that decimal otherwise preserves: 333.95 and 333.950 are the
    /// same number, but <c>ToString()</c> renders them differently, which would log a phantom
    /// revision every time a publisher changed its formatting.
    /// </remarks>
    protected static string? Format(decimal? value) =>
        value?.ToString("G29", CultureInfo.InvariantCulture);
}

/// <summary>One BLS figure for CUUR0000SA0 — a month, a half, or the annual average.</summary>
public sealed record CpiObservationRecord : ObservationRecord
{
    public override string SeriesCode => Entities.CpiObservation.SeriesCodeValue;

    public required short ReferenceYear { get; init; }

    /// <summary>The publisher's period token, verbatim: <c>M01</c>..<c>M13</c>, <c>S01</c>, <c>S02</c>.</summary>
    public required string PeriodCode { get; init; }

    public required PeriodType PeriodType { get; init; }

    public override required DateOnly ReferenceDate { get; init; }

    /// <summary>The index level as published, base 1982-84 = 100.</summary>
    public required decimal IndexValue { get; init; }

    /// <summary>BLS footnote codes, verbatim.</summary>
    public string? Footnotes { get; init; }

    public override string ReferenceLabel => $"{ReferenceYear}/{PeriodCode}";

    /// <remarks>
    /// The footnotes are part of the hash on purpose. BLS flips a footnote from preliminary to
    /// revised without necessarily moving the number, and that transition is itself meaningful
    /// for economic data — it deserves a new vintage rather than being silently swallowed.
    /// </remarks>
    public override byte[] ComputeRowHash() => Hash(Format(IndexValue), Footnotes);
}

/// <summary>One business day of SOFR, with every measure the publisher gave for that day.</summary>
public sealed record SofrDailyRateRecord : ObservationRecord
{
    public override string SeriesCode => Entities.SofrDailyRate.RateTypeValue;

    public required DateOnly EffectiveDate { get; init; }

    public required decimal RatePercent { get; init; }

    public decimal? Percentile1Percent { get; init; }
    public decimal? Percentile25Percent { get; init; }
    public decimal? Percentile75Percent { get; init; }
    public decimal? Percentile99Percent { get; init; }
    public decimal? VolumeUsdBillions { get; init; }

    /// <summary>The publisher's "Y"/"N" revision indicator, verbatim.</summary>
    public string? RevisionIndicator { get; init; }

    public string? FootnoteId { get; init; }

    /// <summary>Same as <see cref="EffectiveDate"/>; a SOFR row's period is the day it covers.</summary>
    public override DateOnly ReferenceDate
    {
        get => EffectiveDate;
        init => EffectiveDate = value;
    }

    public override string ReferenceLabel =>
        EffectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <remarks>
    /// Every measure is hashed, not just the headline rate: a revision that corrected only the
    /// volume or a percentile is still a correction, and hashing the rate alone would file it as
    /// "unchanged" and lose it.
    /// </remarks>
    public override byte[] ComputeRowHash() => Hash(
        Format(RatePercent),
        Format(Percentile1Percent),
        Format(Percentile25Percent),
        Format(Percentile75Percent),
        Format(Percentile99Percent),
        Format(VolumeUsdBillions),
        RevisionIndicator,
        FootnoteId);
}
