// backend/src/DataIntelligence.Infrastructure/Ai/SchemaContextProvider.cs
namespace DataIntelligence.Infrastructure.Ai;

/// <summary>
/// The only schema the model is ever shown — the nine analytics views, described in the model's
/// own words. Keeping this hand-written rather than reflected from the database means the model
/// never sees a table it isn't allowed to query in the first place.
/// </summary>
public static class SchemaContextProvider
{
    public const string Context = """
        You may query ONLY the following SQL Server views. Every column name is exact.

        analytics.vw_Cpi(ReferenceDate, ReferenceYear, PeriodCode, PeriodType, PeriodLabel,
            SeriesCode, SeriesTitle, Unit, IndexValue, RevisionNumber, Footnotes, CollectedAtUtc)
            -- Current CPI figures. PeriodType is 'Month', 'Annual', or 'Semiannual'.

        analytics.vw_CpiMonthlyChange(ReferenceDate, ReferenceYear, PeriodCode, IndexValue,
            PreviousMonthValue, MonthOverMonthPct, YearAgoValue, YearOverYearPct)
            -- Monthly CPI with month-over-month and year-over-year % change (the headline inflation rate).

        analytics.vw_CpiAnnual(ReferenceYear, AnnualValue, FirstHalfValue, SecondHalfValue, CollectedAtUtc)
            -- The annual and semiannual averages BLS publishes, one row per year.

        analytics.vw_Sofr(EffectiveDate, RateType, SeriesTitle, RatePercent, Percentile1Percent,
            Percentile25Percent, Percentile75Percent, Percentile99Percent, VolumeUsdBillions,
            Average30DayPercent, Average90DayPercent, Average180DayPercent, SofrIndexValue,
            RevisionIndicator, RevisionNumber, CollectedAtUtc)
            -- Current SOFR daily rates. VolumeUsdBillions is in billions of dollars, not dollars.

        analytics.vw_SofrAnnual(CalendarYear, BusinessDays, FirstEffectiveDate, LastEffectiveDate,
            AverageRatePercent, MinRatePercent, MaxRatePercent, AverageVolumeUsdBillions, TotalVolumeUsdBillions)
            -- SOFR summarised per calendar year.

        analytics.vw_LatestIndicator(IndicatorCode, IndicatorName, Unit, AsOfDate, Value, CollectedAtUtc)
            -- The single latest CPI row and the single latest SOFR row, side by side.

        analytics.vw_CpiRevision / analytics.vw_SofrRevision
            -- Every vintage of a period that has ever been revised.

        analytics.vw_CollectionHealth(SourceCode, SourceName, RunDate, TotalRuns, SucceededRuns,
            FailedRuns, SkippedRuns, SuccessRatePct, ObservationsInserted, ObservationsRevised, AvgDurationMs)
            -- Daily collector health per source, rolling 30 days.

        Rules:
        - Write exactly one SELECT statement. No comments, no semicolons, no other statement type.
        - Never reference any table or view not listed above.
        - If the question cannot be answered from these views, respond with SQL: null.
        """;
}