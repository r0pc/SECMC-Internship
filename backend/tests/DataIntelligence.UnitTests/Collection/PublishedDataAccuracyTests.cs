using DataIntelligence.Core.Collection;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Enums;
using DataIntelligence.Infrastructure.Collection;
using DataIntelligence.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataIntelligence.UnitTests.Collection;

/// <summary>
/// Parses the real published extracts and checks every figure that comes out against the file it
/// came from (SOW 11.1 — data validation).
/// </summary>
/// <remarks>
/// The other adapter tests use small hand-written payloads to pin one behaviour each. These run
/// the whole of <c>docs/example_data/</c> through and compare cell by cell, which is what catches
/// the failures a curated fixture cannot: a period column read one place to the left, a value
/// truncated at a decimal place the sample happened not to have, a row dropped somewhere in the
/// middle of a century of history.
/// </remarks>
public class PublishedDataAccuracyTests
{
    private static BlsCpiAdapter Bls() => new(
        Options.Create(new CollectionOptions { Bls = new BlsOptions() }),
        NullLogger<BlsCpiAdapter>.Instance);

    private static SofrAdapter Sofr() => new();

    private static readonly DateTime Now = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Guards every other test here.
    /// </summary>
    /// <remarks>
    /// The comparisons below are cell-by-cell against what the reader returns, so they are
    /// self-consistent: a reader that silently produced nothing, or read one column short, would
    /// let all of them pass. These counts are the external anchor — the shape of the extracts as
    /// they stand, arrived at independently.
    /// <para>
    /// A deliberate refresh of <c>docs/example_data/</c> is expected to fail this test. Update the
    /// numbers then, having checked that the difference is the new data and not a parsing change.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheExtractsHaveTheExpectedShape()
    {
        Assert.Equal(1559, PublishedData.CpiCells.Count);
        Assert.Equal(146, PublishedData.SofrOnly.Count);
        Assert.Equal(588, PublishedData.OtherRates.Count);

        // 1913 to date, with the annual and semiannual columns on top of the twelve months.
        Assert.Equal(1913, PublishedData.CpiCells.Min(c => c.Year));
        Assert.Equal(2026, PublishedData.CpiCells.Max(c => c.Year));

        Assert.Equal(
            ["BGCR", "EFFR", "OBFR", "TGCR"],
            PublishedData.OtherRates.Select(r => r.RateType).Distinct().Order(StringComparer.Ordinal));
    }

    // -------------------------------------------------------------------- CPI

    [Fact]
    public void EveryPublishedCpiFigureIsParsedExactly()
    {
        var parsed = Bls().Parse(PublishedData.BlsPayload());

        Assert.Empty(parsed.Rejections);
        Assert.Equal(PublishedData.CpiCells.Count, parsed.EntriesSeen);
        Assert.Equal(PublishedData.CpiCells.Count, parsed.Records.Count);

        var byPeriod = parsed.Records
            .OfType<CpiObservationRecord>()
            .ToDictionary(r => (r.ReferenceYear, r.PeriodCode));

        // Every cell, not a sample: one wrong column mapping would show up in exactly one of
        // these and nowhere else.
        foreach (var cell in PublishedData.CpiCells)
        {
            Assert.True(byPeriod.TryGetValue((cell.Year, cell.PeriodCode), out var record),
                $"No record parsed for {cell.Year}/{cell.PeriodCode}.");

            Assert.Equal(cell.Value, record!.IndexValue);
            Assert.Equal(cell.ReferenceDate, record.ReferenceDate);
        }
    }

    [Fact]
    public void PeriodTypesMatchTheColumnEachFigureCameFrom()
    {
        var parsed = Bls().Parse(PublishedData.BlsPayload());

        foreach (var record in parsed.Records.OfType<CpiObservationRecord>())
        {
            var expected = record.PeriodCode switch
            {
                "M13" => PeriodType.Annual,
                "S01" or "S02" => PeriodType.Semiannual,
                _ => PeriodType.Month
            };

            Assert.Equal(expected, record.PeriodType);
        }

        // And the split is the one the file has: twelve months a year for most years, with the
        // annual and semiannual columns populated for a subset.
        var months = parsed.Records.OfType<CpiObservationRecord>()
            .Count(r => r.PeriodType == PeriodType.Month);

        Assert.Equal(PublishedData.CpiCells.Count(c => c.PeriodCode.StartsWith('M') && c.PeriodCode != "M13"),
            months);
    }

    [Fact]
    public void AMonthThePublisherHasNotReleasedProducesNoRecord()
    {
        // October 2025 is blank in the extract. A gap must stay a gap: writing zero, or carrying
        // September forward, would put a number in the database that BLS never published.
        Assert.DoesNotContain(PublishedData.CpiCells, c => c is { Year: 2025, PeriodCode: "M10" });

        var parsed = Bls().Parse(PublishedData.BlsPayload());

        Assert.DoesNotContain(parsed.Records.OfType<CpiObservationRecord>(),
            r => r is { ReferenceYear: 2025, PeriodCode: "M10" });

        // The months either side did land, so this is an absent cell rather than a broken year.
        Assert.Contains(parsed.Records.OfType<CpiObservationRecord>(),
            r => r is { ReferenceYear: 2025, PeriodCode: "M09" });
        Assert.Contains(parsed.Records.OfType<CpiObservationRecord>(),
            r => r is { ReferenceYear: 2025, PeriodCode: "M11" });
    }

    [Theory]
    // The first figure BLS ever published, at the one decimal place of the era.
    [InlineData(1913, "M01", 9.8)]
    // Either side of the 2007 change to three decimal places.
    [InlineData(2006, "M12", 201.8)]
    [InlineData(2007, "M01", 202.416)]
    // The most recent month in the extract, and the annual and half-year figures for 2025.
    [InlineData(2026, "M06", 333.952)]
    [InlineData(2025, "M13", 321.943)]
    [InlineData(2025, "S01", 320.229)]
    [InlineData(2025, "S02", 324.000)]
    public void KnownCpiFiguresSurviveTheRoundTrip(int year, string periodCode, double expected)
    {
        var record = Bls().Parse(PublishedData.BlsPayload()).Records
            .OfType<CpiObservationRecord>()
            .Single(r => r.ReferenceYear == year && r.PeriodCode == periodCode);

        Assert.Equal((decimal)expected, record.IndexValue);
    }

    [Fact]
    public void TrailingZeroesInThePublishedTextDoNotBecomeARevision()
    {
        // 2025's second half is published as "324.000". Parsed and re-hashed it must equal the
        // same figure written "324", or the first collection after a formatting change upstream
        // would restate the whole series.
        var parsed = Bls().Parse(PublishedData.BlsPayload()).Records
            .OfType<CpiObservationRecord>()
            .Single(r => r is { ReferenceYear: 2025, PeriodCode: "S02" });

        var terse = parsed with { IndexValue = 324m };

        Assert.Equal(parsed.ComputeRowHash(), terse.ComputeRowHash());
    }

    // ------------------------------------------------------------------- SOFR

    [Fact]
    public void EveryPublishedSofrDayIsParsedExactly()
    {
        var parsed = Sofr().Parse(PublishedData.SofrPayload());

        Assert.Equal(PublishedData.SofrRows.Count, parsed.EntriesSeen);
        Assert.Equal(PublishedData.SofrOnly.Count, parsed.Records.Count);

        var byDate = parsed.Records
            .OfType<SofrDailyRateRecord>()
            .ToDictionary(r => r.EffectiveDate);

        foreach (var row in PublishedData.SofrOnly)
        {
            Assert.True(byDate.TryGetValue(row.EffectiveDate, out var record),
                $"No record parsed for {row.EffectiveDate:yyyy-MM-dd}.");

            Assert.Equal(row.Rate, record!.RatePercent);
            Assert.Equal(row.Percentile1, record.Percentile1Percent);
            Assert.Equal(row.Percentile25, record.Percentile25Percent);
            Assert.Equal(row.Percentile75, record.Percentile75Percent);
            Assert.Equal(row.Percentile99, record.Percentile99Percent);
            Assert.Equal(row.Volume, record.VolumeUsdBillions);
        }
    }

    [Fact]
    public void EveryOtherRateInThePayloadIsRejected()
    {
        // The CSV download carries all five rates. The JSON endpoint returns SOFR alone, so this
        // exercises the guard rather than a routine path — but the guard is what stops another
        // rate reaching SOFR's table if that endpoint is ever changed or broadened.
        var parsed = Sofr().Parse(PublishedData.SofrPayload());

        Assert.Equal(PublishedData.OtherRates.Count, parsed.Rejections.Count);
        Assert.All(parsed.Rejections, r => Assert.Equal(RejectionReason.UnknownSeries, r.Reason));

        Assert.Equal(
            PublishedData.OtherRates.Select(r => r.RateType).Distinct().Order(StringComparer.Ordinal),
            parsed.Rejections.Select(r => r.SeriesCode!).Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheExtractCoversOneCalendarYearOfBusinessDays()
    {
        // The annual window the schema is written against: 1 January to the most recent business
        // day, and nothing outside it.
        var parsed = Sofr().Parse(PublishedData.SofrPayload()).Records
            .OfType<SofrDailyRateRecord>()
            .Select(r => r.EffectiveDate)
            .ToList();

        Assert.All(parsed, d => Assert.Equal(2026, d.Year));

        // Weekdays only — a rate for a Saturday would mean the date was misread.
        Assert.All(parsed, d => Assert.NotEqual(DayOfWeek.Saturday, d.DayOfWeek));
        Assert.All(parsed, d => Assert.NotEqual(DayOfWeek.Sunday, d.DayOfWeek));

        Assert.Equal(parsed.Count, parsed.Distinct().Count());
    }

    [Fact]
    public void KnownSofrDaysSurviveTheRoundTrip()
    {
        var record = Sofr().Parse(PublishedData.SofrPayload()).Records
            .OfType<SofrDailyRateRecord>()
            .Single(r => r.EffectiveDate == new DateOnly(2026, 8, 3));

        Assert.Equal(3.65m, record.RatePercent);
        Assert.Equal(3.61m, record.Percentile1Percent);
        Assert.Equal(3.63m, record.Percentile25Percent);
        Assert.Equal(3.70m, record.Percentile75Percent);
        Assert.Equal(3.73m, record.Percentile99Percent);
        Assert.Equal(3055m, record.VolumeUsdBillions);
    }

    [Fact]
    public void ADayWithNoSofrFigureIsNotInvented()
    {
        // 3 July 2026 is a half-day: the extract carries EFFR and OBFR for it and no SOFR at all.
        Assert.DoesNotContain(PublishedData.SofrOnly, r => r.EffectiveDate == new DateOnly(2026, 7, 3));
        Assert.Contains(PublishedData.OtherRates, r => r.EffectiveDate == new DateOnly(2026, 7, 3));

        var parsed = Sofr().Parse(PublishedData.SofrPayload());

        Assert.DoesNotContain(parsed.Records.OfType<SofrDailyRateRecord>(),
            r => r.EffectiveDate == new DateOnly(2026, 7, 3));
    }

    // ------------------------------------------------------------- validation

    [Fact]
    public void EveryParsedFigureFromBothExtractsPassesValidation()
    {
        // The rules mirror the tables' CHECK constraints, so a rejection here would mean real
        // published data cannot be stored — the schema disagreeing with the publisher.
        var records = Bls().Parse(PublishedData.BlsPayload()).Records
            .Concat(Sofr().Parse(PublishedData.SofrPayload()).Records)
            .ToList();

        var failures = records
            .Select(r => new { Record = r, Failure = ObservationValidator.Validate(r, Now) })
            .Where(x => x.Failure is not null)
            .Select(x => $"{x.Record.SeriesCode} {x.Record.ReferenceLabel}: {x.Failure!.Detail}")
            .ToList();

        Assert.Empty(failures);
    }

    [Fact]
    public void ThePublishedPercentilesAreOrderedOnEveryDay()
    {
        // The ordering CK_Sofr_PercentileOrder enforces. If the real data ever failed this, the
        // constraint would be wrong rather than the data.
        foreach (var row in PublishedData.SofrOnly)
        {
            var ordered = new[] { row.Percentile1, row.Percentile25, row.Percentile75, row.Percentile99 };

            for (var i = 1; i < ordered.Length; i++)
            {
                if (ordered[i - 1] is { } lower && ordered[i] is { } upper)
                {
                    Assert.True(lower <= upper,
                        $"{row.EffectiveDate:yyyy-MM-dd}: percentiles out of order ({lower} then {upper}).");
                }
            }
        }
    }

    [Fact]
    public void EveryPublishedRateIsInsideTheSanityBand()
    {
        foreach (var row in PublishedData.SofrOnly)
        {
            Assert.InRange(row.Rate, ObservationValidator.MinRatePercent, ObservationValidator.MaxRatePercent);
        }
    }
}
