using DataIntelligence.Core.Analytics;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.UnitTests.Analytics;

/// <summary>
/// The arithmetic behind every chart and KPI tile (SOW 11.1). Pure functions, so the rules that
/// decide what a dashboard displays are testable without a database.
/// </summary>
public class SeriesPeriodsTests
{
    [Theory]
    [InlineData(SeriesFrequency.BusinessDaily, PeriodType.Day)]
    [InlineData(SeriesFrequency.Daily, PeriodType.Day)]
    [InlineData(SeriesFrequency.Weekly, PeriodType.Week)]
    [InlineData(SeriesFrequency.Monthly, PeriodType.Month)]
    [InlineData(SeriesFrequency.Quarterly, PeriodType.Quarter)]
    [InlineData(SeriesFrequency.Semiannual, PeriodType.Semiannual)]
    [InlineData(SeriesFrequency.Annual, PeriodType.Annual)]
    public void NativePeriodType_MapsEachFrequency(SeriesFrequency frequency, PeriodType expected) =>
        Assert.Equal(expected, SeriesPeriods.NativePeriodType(frequency));

    [Fact]
    public void ResolveGranularity_HonoursAnExplicitChoice()
    {
        var granularity = SeriesPeriods.ResolveGranularity(
            TrendGranularity.Year,
            SeriesFrequency.BusinessDaily,
            new DateOnly(2025, 1, 1),
            new DateOnly(2025, 2, 1));

        Assert.Equal(TrendGranularity.Year, granularity);
    }

    [Theory]
    // A year of either frequency fits on a chart unbucketed.
    [InlineData(SeriesFrequency.Monthly, 2025, 2026, TrendGranularity.Point)]
    [InlineData(SeriesFrequency.BusinessDaily, 2025, 2026, TrendGranularity.Point)]
    // Five years of business-daily data is ~1,260 points; monthly buckets bring it back.
    [InlineData(SeriesFrequency.BusinessDaily, 2021, 2026, TrendGranularity.Month)]
    // Forty years of monthly data exceeds the target even bucketed by month.
    [InlineData(SeriesFrequency.Monthly, 1986, 2026, TrendGranularity.Quarter)]
    // A century of anything only fits by year.
    [InlineData(SeriesFrequency.BusinessDaily, 1900, 2026, TrendGranularity.Year)]
    public void ResolveGranularity_WidensTheBucketAsTheRangeGrows(
        SeriesFrequency frequency,
        int fromYear,
        int toYear,
        TrendGranularity expected)
    {
        var granularity = SeriesPeriods.ResolveGranularity(
            TrendGranularity.Auto,
            frequency,
            new DateOnly(fromYear, 1, 1),
            new DateOnly(toYear, 1, 1));

        Assert.Equal(expected, granularity);
    }

    [Fact]
    public void ResolveGranularity_HandlesAZeroLengthRange()
    {
        var granularity = SeriesPeriods.ResolveGranularity(
            TrendGranularity.Auto,
            SeriesFrequency.BusinessDaily,
            new DateOnly(2026, 8, 4),
            new DateOnly(2026, 8, 4));

        // A single day cannot be divided by anything, so it stays a point rather than turning
        // into a division by zero.
        Assert.Equal(TrendGranularity.Point, granularity);
    }

    [Theory]
    [InlineData(TrendGranularity.Month, 2025, 6, 17, 2025, 6, 1, 2025, 6, 30)]
    [InlineData(TrendGranularity.Quarter, 2025, 6, 17, 2025, 4, 1, 2025, 6, 30)]
    [InlineData(TrendGranularity.Quarter, 2025, 12, 31, 2025, 10, 1, 2025, 12, 31)]
    [InlineData(TrendGranularity.Year, 2025, 6, 17, 2025, 1, 1, 2025, 12, 31)]
    [InlineData(TrendGranularity.Point, 2025, 6, 17, 2025, 6, 17, 2025, 6, 17)]
    public void BucketBoundaries_CoverTheWholePeriodInclusively(
        TrendGranularity granularity,
        int year, int month, int day,
        int startYear, int startMonth, int startDay,
        int endYear, int endMonth, int endDay)
    {
        var reference = new DateOnly(year, month, day);
        var start = SeriesPeriods.BucketStart(reference, granularity);

        Assert.Equal(new DateOnly(startYear, startMonth, startDay), start);
        Assert.Equal(new DateOnly(endYear, endMonth, endDay), SeriesPeriods.BucketEnd(start, granularity));
    }

    [Fact]
    public void BucketEnd_HandlesALeapFebruary()
    {
        var start = SeriesPeriods.BucketStart(new DateOnly(2028, 2, 14), TrendGranularity.Month);

        Assert.Equal(new DateOnly(2028, 2, 29), SeriesPeriods.BucketEnd(start, TrendGranularity.Month));
    }

    [Theory]
    [InlineData(TrendGranularity.Month, 2025, 7, 2025, 7, 1)]
    [InlineData(TrendGranularity.Quarter, 2025, 1, 2025, 1, 1)]
    [InlineData(TrendGranularity.Quarter, 2025, 4, 2025, 10, 1)]
    [InlineData(TrendGranularity.Year, 2025, 1, 2025, 1, 1)]
    public void BucketStartFromParts_RebuildsWhatTheDatabaseGroupedBy(
        TrendGranularity granularity,
        int year,
        int ordinal,
        int expectedYear,
        int expectedMonth,
        int expectedDay)
    {
        var start = SeriesPeriods.BucketStartFromParts(year, ordinal, granularity);

        Assert.Equal(new DateOnly(expectedYear, expectedMonth, expectedDay), start);
    }

    [Fact]
    public void BucketStartFromParts_RoundTripsWithBucketStart()
    {
        var reference = new DateOnly(2025, 8, 19);

        // The quarter ordinal as the database computes it: ((month - 1) / 3) + 1.
        var ordinal = ((reference.Month - 1) / 3) + 1;

        Assert.Equal(
            SeriesPeriods.BucketStart(reference, TrendGranularity.Quarter),
            SeriesPeriods.BucketStartFromParts(reference.Year, ordinal, TrendGranularity.Quarter));
    }

    [Fact]
    public void PercentChange_ComputesAnOrdinaryRise() =>
        Assert.Equal(10m, SeriesPeriods.PercentChange(110m, 100m));

    [Fact]
    public void PercentChange_IsNullAgainstAZeroBase() =>
        Assert.Null(SeriesPeriods.PercentChange(5m, 0m));

    [Fact]
    public void PercentChange_TreatsAMoveTowardsZeroAsARiseWhenTheBaseIsNegative()
    {
        // -10 to -5 is an increase. Dividing by the signed base would report -50%.
        Assert.Equal(50m, SeriesPeriods.PercentChange(-5m, -10m));
    }

    [Fact]
    public void PercentChange_RoundsToFourDecimals() =>
        Assert.Equal(-66.6667m, SeriesPeriods.PercentChange(1m, 3m));
}
