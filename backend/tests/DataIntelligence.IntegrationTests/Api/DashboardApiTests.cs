using System.Net;
using DataIntelligence.Core.Collection;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.IntegrationTests.Api;

/// <summary>
/// The analytical endpoints: observations, trends, KPIs, and collection health
/// (FR-7, FR-10, FR-2).
/// </summary>
[Collection(DashboardApiCollection.Name)]
public class DashboardApiTests
{
    private const string MonthlyRange = "from=2023-01-01&to=2025-12-31";

    private static readonly string Cpi = DashboardApiFixture.CpiKey;
    private static readonly string Sofr = DashboardApiFixture.SofrKey;

    private readonly HttpClient _client;

    public DashboardApiTests(DashboardApiFixture fixture)
    {
        if (!fixture.IsAvailable)
        {
            throw new InvalidOperationException(fixture.UnavailableReason);
        }

        _client = fixture.Client;
    }

    // ------------------------------------------------------------- observations

    [Fact]
    public async Task GetObservations_DefaultsToCurrentMonthlyFigures()
    {
        var page = await _client.GetJsonAsync<PagedResult<ObservationDto>>(
            $"/api/series/{Cpi}/observations?{MonthlyRange}");

        // The monthly history plus the current vintage of the revised month — and not the
        // annual-average row, which shares 1 January 2024 with that year's January figure.
        Assert.Equal(DashboardApiFixture.MonthlyCount + 1, page.TotalCount);
        Assert.All(page.Items, o => Assert.Equal(PeriodType.Month, o.PeriodType));
        Assert.All(page.Items, o => Assert.True(o.IsCurrent));
        Assert.DoesNotContain(page.Items, o => o.Value == DashboardApiFixture.AnnualValue);

        // Oldest first by default, which is the order a chart wants.
        Assert.Equal(DashboardApiFixture.MonthlyStart, page.Items[0].ReferenceDate);
        Assert.Equal(DashboardApiFixture.MonthlyValue(0), page.Items[0].Value);

        var last = page.Items[^1];

        Assert.Equal(DashboardApiFixture.RevisedMonth, last.ReferenceDate);
        Assert.Equal(DashboardApiFixture.RevisedCurrentValue, last.Value);
        Assert.Equal(1, last.RevisionNumber);
    }

    [Fact]
    public async Task GetObservations_PeriodTypeAnnual_ReturnsTheAnnualAverageRow()
    {
        // The row that could not previously be stored at all: it shares a reference date with
        // January, and only the period code tells them apart.
        var page = await _client.GetJsonAsync<PagedResult<ObservationDto>>(
            $"/api/series/{Cpi}/observations?{MonthlyRange}&periodType={PeriodType.Annual}");

        var annual = Assert.Single(page.Items);

        Assert.Equal(new DateOnly(DashboardApiFixture.AnnualYear, 1, 1), annual.ReferenceDate);
        Assert.Equal(DashboardApiFixture.AnnualValue, annual.Value);
        Assert.Equal(CpiPeriod.AnnualCode, annual.PeriodCode);

        // And the monthly read for that same date returns January's figure, not this one.
        var january = await _client.GetJsonAsync<PagedResult<ObservationDto>>(
            $"/api/series/{Cpi}/observations"
            + $"?from={DashboardApiFixture.AnnualYear}-01-01&to={DashboardApiFixture.AnnualYear}-01-01");

        Assert.Equal(DashboardApiFixture.MonthlyValue(0), Assert.Single(january.Items).Value);
    }

    [Fact]
    public async Task GetObservations_IncludeRevisions_ReturnsEveryVintage()
    {
        var page = await _client.GetJsonAsync<PagedResult<ObservationDto>>(
            $"/api/series/{Cpi}/observations"
            + $"?from={DashboardApiFixture.RevisedMonth:yyyy-MM-dd}"
            + $"&to={DashboardApiFixture.RevisedMonth:yyyy-MM-dd}&includeRevisions=true");

        Assert.Equal(2, page.TotalCount);

        var superseded = page.Items.Single(o => !o.IsCurrent);

        Assert.Equal(DashboardApiFixture.RevisedOriginalValue, superseded.Value);
        Assert.Equal(0, superseded.RevisionNumber);
        Assert.NotNull(superseded.SupersededAtPkt);

        var current = page.Items.Single(o => o.IsCurrent);

        Assert.Equal(DashboardApiFixture.RevisedCurrentValue, current.Value);
    }

    [Fact]
    public async Task GetObservations_AsOf_ReturnsTheVintageInForceAtThatInstant()
    {
        // Half an hour after the first collection: the revision has not arrived yet.
        var asOf = DashboardApiFixture.FirstCollectionUtc.AddMinutes(30);

        var page = await _client.GetJsonAsync<PagedResult<ObservationDto>>(
            $"/api/series/{Cpi}/observations"
            + $"?from={DashboardApiFixture.RevisedMonth:yyyy-MM-dd}"
            + $"&to={DashboardApiFixture.RevisedMonth:yyyy-MM-dd}&asOfUtc={asOf:O}");

        var observation = Assert.Single(page.Items);

        Assert.Equal(DashboardApiFixture.RevisedOriginalValue, observation.Value);
        Assert.False(observation.IsCurrent);
    }

    [Fact]
    public async Task GetObservations_ReadsOneMeasureOfASofrDay()
    {
        var page = await _client.GetJsonAsync<PagedResult<ObservationDto>>(
            $"/api/series/{DashboardApiFixture.SofrVolumeKey}/observations"
            + "?from=2025-01-01&to=2025-02-28");

        Assert.Equal(DashboardApiFixture.DailyPoints.Length, page.TotalCount);

        // SOFR rows have no period column — every one of them is a business day.
        Assert.All(page.Items, o => Assert.Null(o.PeriodType));
        Assert.All(page.Items, o => Assert.Null(o.PeriodCode));

        Assert.Equal(DashboardApiFixture.DailyPoints[0].Volume, page.Items[0].Value);
    }

    [Fact]
    public async Task GetObservations_SortDescending_ReturnsNewestFirst()
    {
        var page = await _client.GetJsonAsync<PagedResult<ObservationDto>>(
            $"/api/series/{Cpi}/observations"
            + $"?{MonthlyRange}&sort={SortDirection.Descending}&pageSize=1");

        var newest = Assert.Single(page.Items);

        Assert.Equal(DashboardApiFixture.RevisedMonth, newest.ReferenceDate);
        Assert.Equal(DashboardApiFixture.MonthlyCount + 1, page.TotalCount);
    }

    [Fact]
    public async Task GetObservations_RangeWithNoData_ReturnsAnEmptyPageRatherThan404()
    {
        // A series that exists but has nothing in the requested window is not an error, and the
        // distinction matters to a caller: 404 means "you asked for the wrong thing", an empty
        // page means "there is nothing there yet".
        var page = await _client.GetJsonAsync<PagedResult<ObservationDto>>(
            $"/api/series/{Cpi}/observations?from=1990-01-01&to=1990-12-31");

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(0, page.TotalPages);
        Assert.False(page.HasNextPage);
        Assert.False(page.HasPreviousPage);
    }

    [Fact]
    public async Task GetObservations_UnknownSeries_Returns404()
    {
        var response = await _client.GetAsync("/api/series/not-a-series/observations");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetObservations_BackwardsRange_Returns400()
    {
        var response = await _client.GetAsync(
            $"/api/series/{Cpi}/observations?from=2025-12-31&to=2024-01-01");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ------------------------------------------------------------------- trends

    [Fact]
    public async Task GetTrend_MonthGranularity_AggregatesDailyObservations()
    {
        var lines = await _client.GetJsonAsync<List<TrendSeriesDto>>(
            $"/api/dashboard/trend?seriesKeys={Sofr}"
            + $"&from=2025-01-01&to=2025-02-28&granularity={TrendGranularity.Month}");

        var line = Assert.Single(lines);

        Assert.Equal(TrendGranularity.Month, line.Granularity);
        Assert.Equal(2, line.Points.Count);

        var january = line.Points[0];

        Assert.Equal(new DateOnly(2025, 1, 1), january.BucketStart);
        Assert.Equal(new DateOnly(2025, 1, 31), january.BucketEnd);
        Assert.Equal(5, january.ObservationCount);
        Assert.Equal(5.20m, january.Value, 2);
        Assert.Equal(5.00m, january.Minimum, 2);
        Assert.Equal(5.40m, january.Maximum, 2);

        var february = line.Points[1];

        Assert.Equal(new DateOnly(2025, 2, 1), february.BucketStart);
        Assert.Equal(3, february.ObservationCount);
        Assert.Equal(4.50m, february.Value, 2);
    }

    [Fact]
    public async Task GetTrend_AutoGranularity_LeavesAShortRangeUnbucketed()
    {
        var lines = await _client.GetJsonAsync<List<TrendSeriesDto>>(
            $"/api/dashboard/trend?seriesKeys={Sofr}&from=2025-01-01&to=2025-02-28");

        var line = Assert.Single(lines);

        Assert.Equal(TrendGranularity.Point, line.Granularity);
        Assert.Equal(DashboardApiFixture.DailyPoints.Length, line.Points.Count);
        Assert.All(line.Points, p => Assert.Equal(1, p.ObservationCount));
        Assert.All(line.Points, p => Assert.Equal(p.BucketStart, p.BucketEnd));

        // Ascending, and carrying the value as published.
        Assert.Equal(DashboardApiFixture.DailyPoints[0].Date, line.Points[0].BucketStart);
        Assert.Equal(DashboardApiFixture.DailyPoints[0].Rate, line.Points[0].Value, 2);
    }

    [Fact]
    public async Task GetTrend_ExcludesTheAnnualAverageFromAMonthlyLine()
    {
        // The reason CPI's period type is filtered everywhere: 2024's annual average shares a
        // date with January, and charting both would draw the year twice.
        var lines = await _client.GetJsonAsync<List<TrendSeriesDto>>(
            $"/api/dashboard/trend?seriesKeys={Cpi}&from=2024-01-01&to=2024-01-31"
            + $"&granularity={TrendGranularity.Month}");

        var point = Assert.Single(Assert.Single(lines).Points);

        Assert.Equal(1, point.ObservationCount);
        Assert.Equal(DashboardApiFixture.MonthlyValue(0), point.Value, 2);
    }

    [Fact]
    public async Task GetTrend_ExcludesSupersededVintages()
    {
        var lines = await _client.GetJsonAsync<List<TrendSeriesDto>>(
            $"/api/dashboard/trend?seriesKeys={Cpi}"
            + $"&from={DashboardApiFixture.RevisedMonth:yyyy-MM-dd}&to=2025-12-31"
            + $"&granularity={TrendGranularity.Month}");

        var point = Assert.Single(Assert.Single(lines).Points);

        // One observation in the bucket, not two: the revision replaced the original rather than
        // joining it, so the mean is the current value and not the average of both vintages.
        Assert.Equal(1, point.ObservationCount);
        Assert.Equal(DashboardApiFixture.RevisedCurrentValue, point.Value, 2);
    }

    [Fact]
    public async Task GetTrend_PreservesTheRequestedSeriesOrder()
    {
        var lines = await _client.GetJsonAsync<List<TrendSeriesDto>>(
            $"/api/dashboard/trend?seriesKeys={Sofr},{Cpi}&from=2024-01-01&to=2025-12-31");

        Assert.Equal(2, lines.Count);
        Assert.Equal(Sofr, lines[0].SeriesKey);
        Assert.Equal(Cpi, lines[1].SeriesKey);

        // Units travel with each line: these two are not comparable on one axis.
        Assert.NotEqual(lines[0].Unit, lines[1].Unit);
    }

    [Fact]
    public async Task GetTrend_UnknownSeriesKey_IsSkippedRatherThanFailingTheRequest()
    {
        var lines = await _client.GetJsonAsync<List<TrendSeriesDto>>(
            $"/api/dashboard/trend?seriesKeys=not-a-series,{Cpi}&from=2024-01-01&to=2025-12-31");

        Assert.Equal(Cpi, Assert.Single(lines).SeriesKey);
    }

    [Fact]
    public async Task GetTrend_TooManySeries_Returns400()
    {
        var keys = string.Join(',', Enumerable.Range(1, 11).Select(i => $"series{i}"));

        var response = await _client.GetAsync($"/api/dashboard/trend?seriesKeys={keys}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --------------------------------------------------------------------- KPIs

    [Fact]
    public async Task GetKpis_ComputesChangeAgainstPreviousAndYearAgoReleases()
    {
        var kpis = await _client.GetJsonAsync<List<SeriesKpiDto>>(
            $"/api/dashboard/kpis?seriesKeys={Cpi}");

        var kpi = Assert.Single(kpis);

        Assert.NotNull(kpi.Latest);
        Assert.Equal(DashboardApiFixture.RevisedMonth, kpi.Latest!.ReferenceDate);
        Assert.Equal(DashboardApiFixture.RevisedCurrentValue, kpi.Latest.Value);

        // The release before it: the last of the plain monthly rows.
        var previousValue = DashboardApiFixture.MonthlyValue(DashboardApiFixture.MonthlyCount - 1);

        Assert.Equal(previousValue, kpi.PreviousValue);
        Assert.Equal(new DateOnly(2025, 11, 1), kpi.PreviousReferenceDate);
        Assert.Equal(DashboardApiFixture.RevisedCurrentValue - previousValue, kpi.ChangeFromPrevious);

        // A year before December 2025 is December 2024 — the twelfth seeded month.
        var yearAgoValue = DashboardApiFixture.MonthlyValue(11);

        Assert.Equal(new DateOnly(2024, 12, 1), kpi.YearAgoReferenceDate);
        Assert.Equal(yearAgoValue, kpi.YearAgoValue);
        Assert.Equal(DashboardApiFixture.RevisedCurrentValue - yearAgoValue, kpi.ChangeFromYearAgo);

        var expectedYoyPercent =
            (DashboardApiFixture.RevisedCurrentValue - yearAgoValue) / yearAgoValue * 100m;

        Assert.NotNull(kpi.PercentChangeFromYearAgo);
        Assert.Equal(expectedYoyPercent, kpi.PercentChangeFromYearAgo!.Value, 3);
    }

    [Fact]
    public async Task GetKpis_ReadsEachSofrMeasureIndependently()
    {
        var kpis = await _client.GetJsonAsync<List<SeriesKpiDto>>(
            $"/api/dashboard/kpis?seriesKeys={Sofr},{DashboardApiFixture.SofrVolumeKey}");

        Assert.Equal(2, kpis.Count);

        var latest = DashboardApiFixture.DailyPoints[^1];
        var previous = DashboardApiFixture.DailyPoints[^2];

        var rate = kpis.Single(k => k.SeriesKey == Sofr);

        Assert.Equal(latest.Rate, rate.Latest!.Value);
        Assert.Equal(latest.Rate - previous.Rate, rate.ChangeFromPrevious);

        var volume = kpis.Single(k => k.SeriesKey == DashboardApiFixture.SofrVolumeKey);

        Assert.Equal(latest.Volume, volume.Latest!.Value);
        Assert.Equal(latest.Volume - previous.Volume, volume.ChangeFromPrevious);
    }

    [Fact]
    public async Task GetKpis_UnknownKey_IsSkipped()
    {
        var kpis = await _client.GetJsonAsync<List<SeriesKpiDto>>(
            $"/api/dashboard/kpis?seriesKeys=not-a-series,{Cpi}");

        Assert.Equal(Cpi, Assert.Single(kpis).SeriesKey);
    }

    [Fact]
    public async Task GetKpis_WithoutSeriesKeys_Returns400()
    {
        var response = await _client.GetAsync("/api/dashboard/kpis");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------- summary and run health

    [Fact]
    public async Task GetSummary_ReportsPerDatasetCountsAndTheSpanOfHistory()
    {
        var summary = await _client.GetJsonAsync<DashboardSummaryDto>("/api/dashboard/summary");

        Assert.Equal(2, summary.SourceCount);
        Assert.Equal(7, summary.SeriesCount);

        // Current vintages only: the superseded December row is not counted twice. The annual
        // average is counted — it is a stored row — but does not extend the monthly span.
        Assert.Equal(DashboardApiFixture.MonthlyCount + 1 + 1, summary.CpiObservationCount);
        Assert.Equal(DashboardApiFixture.DailyPoints.Length, summary.SofrObservationCount);

        Assert.Equal(DashboardApiFixture.MonthlyStart, summary.EarliestCpiMonth);
        Assert.Equal(DashboardApiFixture.RevisedMonth, summary.LatestCpiMonth);
        Assert.Equal(DashboardApiFixture.DailyPoints[0].Date, summary.EarliestSofrDate);
        Assert.Equal(DashboardApiFixture.DailyPoints[^1].Date, summary.LatestSofrDate);

        Assert.NotNull(summary.LastCollectionAtPkt);
        Assert.Equal(2, summary.Sources.Count);
    }

    [Fact]
    public async Task GetHealth_ReportsSuccessRateAndConsecutiveFailures()
    {
        var health = await _client.GetJsonAsync<List<SourceHealthDto>>("/api/collection/health");

        var bls = health.Single(h => h.SourceCode == DataSource.BlsCpiCode);

        Assert.Equal(3, bls.TotalRuns);
        Assert.Equal(2, bls.SucceededRuns);
        Assert.Equal(1, bls.FailedRuns);
        Assert.Equal(66.67m, bls.SuccessRatePercent!.Value, 2);

        // The most recent run failed, and nothing has succeeded since.
        Assert.Equal(CollectionRunStatus.Failed, bls.LastRunStatus);
        Assert.Equal(CollectionFailureCategory.HttpError, bls.LastFailureCategory);
        Assert.Equal(1, bls.ConsecutiveFailures);
        Assert.NotNull(bls.LastSuccessAtPkt);

        var sofr = health.Single(h => h.SourceCode == DataSource.NyFedSofrCode);

        Assert.Equal(0, sofr.ConsecutiveFailures);
        Assert.Equal(100m, sofr.SuccessRatePercent!.Value, 2);
    }

    [Fact]
    public async Task GetCollectionRuns_FailuresOnly_NarrowsToFailedAndPartialRuns()
    {
        var page = await _client.GetJsonAsync<PagedResult<CollectionRunDto>>(
            "/api/collection/runs?failuresOnly=true");

        var run = Assert.Single(page.Items);

        Assert.Equal(CollectionRunStatus.Failed, run.Status);
        Assert.Equal(CollectionFailureCategory.HttpError, run.FailureCategory);
        Assert.Equal("BLS returned 503.", run.ErrorMessage);
        Assert.Equal(DataSource.BlsCpiCode, run.SourceCode);

        // The computed duration column comes back populated.
        Assert.Equal(4000, run.DurationMs);
    }

    [Fact]
    public async Task GetCollectionRuns_NewestFirst_AndFilterableBySource()
    {
        var page = await _client.GetJsonAsync<PagedResult<CollectionRunDto>>(
            $"/api/collection/runs?dataSourceId={DataSource.BlsCpiId}");

        Assert.Equal(3, page.TotalCount);
        Assert.All(page.Items, r => Assert.Equal(DataSource.BlsCpiId, r.DataSourceId));
        Assert.Equal(CollectionRunStatus.Failed, page.Items[0].Status);

        var run = await _client.GetJsonAsync<CollectionRunDto>(
            $"/api/collection/runs/{page.Items[0].CollectionRunId}");

        Assert.Equal(page.Items[0].CollectionRunId, run.CollectionRunId);
    }

    [Fact]
    public async Task GetCollectionRun_UnknownId_Returns404()
    {
        var response = await _client.GetAsync("/api/collection/runs/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
