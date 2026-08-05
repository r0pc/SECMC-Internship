using System.Net;
using System.Net.Http.Json;
using DataIntelligence.Core.Analytics;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.IntegrationTests.Api;

/// <summary>
/// The catalogue endpoints the dashboards navigate: sources and series (FR-7).
/// </summary>
[Collection(DashboardApiCollection.Name)]
public class CatalogApiTests
{
    private readonly HttpClient _client;

    public CatalogApiTests(DashboardApiFixture fixture)
    {
        if (!fixture.IsAvailable)
        {
            throw new InvalidOperationException(fixture.UnavailableReason);
        }

        _client = fixture.Client;
    }

    [Fact]
    public async Task Health_ReportsDatabaseReachable()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"database\":\"ok\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GetSources_ReturnsBothSeededPublishers()
    {
        var sources = await _client.GetJsonAsync<List<DataSourceDto>>("/api/sources");

        Assert.Equal(2, sources.Count);

        var bls = sources.Single(s => s.Code == DataSource.BlsCpiCode);

        Assert.Equal("U.S. Bureau of Labor Statistics", bls.Publisher);
        Assert.Equal(SourceAccessMethod.RestApi, bls.AccessMethod);

        // One CPI series is in scope; SOFR contributes six measures of one table.
        Assert.Equal(1, bls.SeriesCount);
        Assert.Equal(6, sources.Single(s => s.Code == DataSource.NyFedSofrCode).SeriesCount);
    }

    [Fact]
    public async Task UpdateSource_ChangesPollingSettingsOnly()
    {
        var response = await _client.PatchAsJsonAsync(
            $"/api/sources/{DataSource.NyFedSofrId}",
            new DataSourceUpdateRequest { RequestTimeoutSec = 45 },
            DashboardApiFixture.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await response.Content.ReadFromJsonAsync<DataSourceDto>(DashboardApiFixture.Json);

        Assert.NotNull(updated);
        Assert.Equal(45, updated!.RequestTimeoutSec);

        // Omitted fields are left alone rather than reset to their defaults.
        Assert.Equal(DataSource.NyFedSofrCode, updated.Code);
        Assert.True(updated.IsEnabled);

        await _client.PatchAsJsonAsync(
            $"/api/sources/{DataSource.NyFedSofrId}",
            new DataSourceUpdateRequest { RequestTimeoutSec = 30 },
            DashboardApiFixture.Json);
    }

    [Fact]
    public async Task UpdateSource_RejectsOutOfRangeInterval()
    {
        var response = await _client.PatchAsJsonAsync(
            $"/api/sources/{DataSource.BlsCpiId}",
            new DataSourceUpdateRequest { CollectionIntervalMinutes = 5000 },
            DashboardApiFixture.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetSeries_ReturnsTheWholeCatalogue()
    {
        var page = await _client.GetJsonAsync<PagedResult<SeriesDto>>("/api/series");

        Assert.Equal(SeriesCatalog.All.Count, page.TotalCount);
    }

    [Fact]
    public async Task GetSeries_AttachesTheCurrentVintageAsTheLatestValue()
    {
        var page = await _client.GetJsonAsync<PagedResult<SeriesDto>>(
            $"/api/series?dataSourceId={DataSource.BlsCpiId}");

        var cpi = Assert.Single(page.Items);

        Assert.Equal(DashboardApiFixture.CpiKey, cpi.SeriesKey);
        Assert.Equal(Dataset.Cpi, cpi.Dataset);
        Assert.Equal(SeriesFrequency.Monthly, cpi.Frequency);
        Assert.Equal(SeasonalAdjustment.NotSeasonallyAdjusted, cpi.SeasonalAdjustment);
        Assert.Equal(CpiObservation.SeriesCodeValue, cpi.PublisherCode);

        // The revised month's current vintage, not the superseded one — and a monthly figure
        // rather than the annual average that shares a date with January.
        Assert.NotNull(cpi.Latest);
        Assert.Equal(DashboardApiFixture.RevisedMonth, cpi.Latest!.ReferenceDate);
        Assert.Equal(DashboardApiFixture.RevisedCurrentValue, cpi.Latest.Value);
    }

    [Fact]
    public async Task GetSeries_ReadsEachSofrMeasureFromTheSameRow()
    {
        // Six catalogue entries over one table: the measures are columns of a business day, so
        // they share an as-of date and differ only in value and unit.
        var page = await _client.GetJsonAsync<PagedResult<SeriesDto>>(
            $"/api/series?dataSourceId={DataSource.NyFedSofrId}");

        var latestDay = DashboardApiFixture.DailyPoints[^1];

        var rate = page.Items.Single(s => s.SeriesKey == DashboardApiFixture.SofrKey);
        var volume = page.Items.Single(s => s.SeriesKey == DashboardApiFixture.SofrVolumeKey);

        Assert.Equal(latestDay.Date, rate.Latest!.ReferenceDate);
        Assert.Equal(latestDay.Rate, rate.Latest.Value);
        Assert.Equal("Percent per annum", rate.Unit);

        Assert.Equal(latestDay.Date, volume.Latest!.ReferenceDate);
        Assert.Equal(latestDay.Volume, volume.Latest.Value);
        Assert.Equal("USD billions", volume.Unit);
    }

    [Fact]
    public async Task GetSeries_FiltersByDataset()
    {
        var page = await _client.GetJsonAsync<PagedResult<SeriesDto>>("/api/series?dataset=Sofr");

        Assert.Equal(6, page.TotalCount);
        Assert.All(page.Items, s => Assert.Equal(Dataset.Sofr, s.Dataset));
    }

    [Fact]
    public async Task GetSeries_SearchMatchesTitleKeyAndPublisherCode()
    {
        var byPublisherCode = await _client.GetJsonAsync<PagedResult<SeriesDto>>(
            $"/api/series?search={CpiObservation.SeriesCodeValue}");

        Assert.Equal(DashboardApiFixture.CpiKey, Assert.Single(byPublisherCode.Items).SeriesKey);

        var byKey = await _client.GetJsonAsync<PagedResult<SeriesDto>>("/api/series?search=sofr.p");

        Assert.Equal(4, byKey.TotalCount);

        var byTitle = await _client.GetJsonAsync<PagedResult<SeriesDto>>("/api/series?search=percentile");

        Assert.Equal(4, byTitle.TotalCount);
    }

    [Fact]
    public async Task GetSeries_PagesAndClampsPageSize()
    {
        var page = await _client.GetJsonAsync<PagedResult<SeriesDto>>("/api/series?page=2&pageSize=3");

        Assert.Equal(2, page.Page);
        Assert.Equal(3, page.PageSize);
        Assert.Equal(3, page.Items.Count);
        Assert.True(page.HasPreviousPage);

        // Seven series, so page 2 of 3 is not the last.
        Assert.True(page.HasNextPage);

        var clamped = await _client.GetJsonAsync<PagedResult<SeriesDto>>("/api/series?pageSize=100000");

        Assert.Equal(PageRequest.MaxPageSize, clamped.PageSize);
    }

    [Fact]
    public async Task GetSeriesByKey_IsCaseInsensitive()
    {
        var series = await _client.GetJsonAsync<SeriesDto>("/api/series/CPI");

        Assert.Equal(DashboardApiFixture.CpiKey, series.SeriesKey);
    }

    [Fact]
    public async Task GetSeriesByKey_UnknownKey_Returns404()
    {
        var response = await _client.GetAsync("/api/series/not-a-series");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Series_HasNoWriteSurface()
    {
        // The catalogue is fixed by the schema and the code that reads it; there is nothing to
        // edit that would not simply make the platform disagree with itself.
        var put = await _client.PutAsJsonAsync(
            $"/api/series/{DashboardApiFixture.CpiKey}",
            new { title = "Renamed" },
            DashboardApiFixture.Json);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);
    }

    [Fact]
    public async Task Categories_AreGone()
    {
        // The grouping dimension existed to organise a registry of many series. Two datasets in
        // two tables need no such thing.
        var response = await _client.GetAsync("/api/categories");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
