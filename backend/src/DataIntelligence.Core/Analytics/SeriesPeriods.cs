using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Analytics;

/// <summary>
/// Pure rules about which observations belong on a chart and how they bucket. No database
/// access, so the arithmetic that decides what a dashboard displays is unit-testable on its
/// own (SOW 11.1).
/// </summary>
public static class SeriesPeriods
{
    /// <summary>
    /// The period length a series' regular releases describe.
    /// </summary>
    /// <remarks>
    /// The reason every analytical query filters on this. A BLS response for a monthly series can
    /// carry more than monthly rows — <c>BlsPeriod</c> maps M13 to <see cref="PeriodType.Annual"/>
    /// and S01/S02 to <see cref="PeriodType.Semiannual"/> — and charting them together plots the
    /// year's average as if it were a thirteenth month. Averaging over that double-counts the
    /// year: a wrong number that looks entirely plausible.
    /// <para>
    /// Today the write side cannot actually store both. <c>UQ_Observation_Current</c> is keyed on
    /// (SeriesId, ReferenceDate) without the period type, and M13 is dated 1 January — the same
    /// reference date as that year's M01 row — so the pair collides. The collector does not
    /// request annual averages, which is why the conflict is latent rather than breaking cycles.
    /// Filtering here is what keeps the read side correct either way.
    /// </para>
    /// </remarks>
    public static PeriodType NativePeriodType(SeriesFrequency frequency) => frequency switch
    {
        SeriesFrequency.BusinessDaily or SeriesFrequency.Daily => PeriodType.Day,
        SeriesFrequency.Weekly => PeriodType.Week,
        SeriesFrequency.Monthly => PeriodType.Month,
        SeriesFrequency.Quarterly => PeriodType.Quarter,
        SeriesFrequency.Semiannual => PeriodType.Semiannual,
        SeriesFrequency.Annual => PeriodType.Annual,
        _ => PeriodType.Month
    };

    /// <summary>Approximate releases per year, used to size an automatic bucket.</summary>
    public static int ReleasesPerYear(SeriesFrequency frequency) => frequency switch
    {
        SeriesFrequency.BusinessDaily => 252,
        SeriesFrequency.Daily => 365,
        SeriesFrequency.Weekly => 52,
        SeriesFrequency.Monthly => 12,
        SeriesFrequency.Quarterly => 4,
        SeriesFrequency.Semiannual => 2,
        SeriesFrequency.Annual => 1,
        _ => 12
    };

    /// <summary>
    /// Widens the bucket until the chart holds a readable number of points, based on the
    /// densest series in the request and the span asked for.
    /// </summary>
    /// <remarks>
    /// Targets roughly 400 points. Below that the bucket stays as fine as the data allows;
    /// above it, a line chart is drawing more points than a screen has pixels and every one of
    /// them still has to be serialised, queried, and parsed inside the 3-second budget
    /// (NFR Performance).
    /// </remarks>
    public static TrendGranularity ResolveGranularity(
        TrendGranularity requested,
        SeriesFrequency densestFrequency,
        DateOnly from,
        DateOnly to)
    {
        if (requested != TrendGranularity.Auto)
        {
            return requested;
        }

        const int targetPoints = 400;

        var years = Math.Max((to.DayNumber - from.DayNumber) / 365.25, 1d / 365.25);
        var estimatedPoints = years * ReleasesPerYear(densestFrequency);

        if (estimatedPoints <= targetPoints)
        {
            return TrendGranularity.Point;
        }

        if (years * 12 <= targetPoints)
        {
            return TrendGranularity.Month;
        }

        return years * 4 <= targetPoints ? TrendGranularity.Quarter : TrendGranularity.Year;
    }

    /// <summary>First day of the bucket a reference date falls in.</summary>
    public static DateOnly BucketStart(DateOnly referenceDate, TrendGranularity granularity) =>
        granularity switch
        {
            TrendGranularity.Month => new DateOnly(referenceDate.Year, referenceDate.Month, 1),
            TrendGranularity.Quarter => new DateOnly(
                referenceDate.Year, ((referenceDate.Month - 1) / 3 * 3) + 1, 1),
            TrendGranularity.Year => new DateOnly(referenceDate.Year, 1, 1),
            _ => referenceDate
        };

    /// <summary>
    /// Last day of the bucket. Inclusive, so a caller can label an axis without having to know
    /// whether the API's ranges are half-open.
    /// </summary>
    public static DateOnly BucketEnd(DateOnly bucketStart, TrendGranularity granularity) =>
        granularity switch
        {
            TrendGranularity.Month => bucketStart.AddMonths(1).AddDays(-1),
            TrendGranularity.Quarter => bucketStart.AddMonths(3).AddDays(-1),
            TrendGranularity.Year => bucketStart.AddYears(1).AddDays(-1),
            _ => bucketStart
        };

    /// <summary>
    /// Rebuilds a bucket start from the parts SQL Server can group by. The database groups on
    /// <c>DATEPART</c> integers — constructing a date inside the query is not translatable — so
    /// the ordinal comes back and becomes a date here.
    /// </summary>
    /// <param name="year">Calendar year of the bucket.</param>
    /// <param name="ordinal">1-based month for Month, 1-based quarter for Quarter, ignored for Year.</param>
    /// <param name="granularity">The bucket width the parts were grouped by.</param>
    public static DateOnly BucketStartFromParts(int year, int ordinal, TrendGranularity granularity) =>
        granularity switch
        {
            TrendGranularity.Month => new DateOnly(year, ordinal, 1),
            TrendGranularity.Quarter => new DateOnly(year, ((ordinal - 1) * 3) + 1, 1),
            _ => new DateOnly(year, 1, 1)
        };

    /// <summary>
    /// Percentage change from <paramref name="previous"/> to <paramref name="current"/>.
    /// </summary>
    /// <remarks>
    /// Null when the base is zero rather than a division by zero or an infinity that would
    /// serialise as <c>null</c> anyway — the KPI tile should render "n/a", not "∞%". Rounded to
    /// four decimals so a repeating fraction does not arrive as a twenty-digit number.
    /// </remarks>
    public static decimal? PercentChange(decimal current, decimal previous) =>
        previous == 0m ? null : Math.Round((current - previous) / Math.Abs(previous) * 100m, 4);
}
