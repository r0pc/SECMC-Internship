using System.Net;
using System.Net.Http.Json;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.IntegrationTests.Api;

/// <summary>
/// The catalogue endpoints the dashboards navigate: sources, categories, and series (FR-7).
/// </summary>
[Collection(DashboardApiCollection.Name)]
public class CatalogApiTests
{
    private readonly DashboardApiFixture _fixture;
    private readonly HttpClient _client;

    public CatalogApiTests(DashboardApiFixture fixture)
    {
        if (!fixture.IsAvailable)
        {
            throw new InvalidOperationException(fixture.UnavailableReason);
        }

        _fixture = fixture;
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

        // Four CPI series are seeded by the migration.
        Assert.Equal(4, bls.SeriesCount);
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
    public async Task GetSeries_ReturnsSeededCatalogueWithLatestValue()
    {
        var page = await _client.GetJsonAsync<PagedResult<SeriesDto>>(
            $"/api/series?dataSourceId={DataSource.BlsCpiId}");

        Assert.Equal(4, page.TotalCount);

        var monthly = page.Items.Single(s => s.SeriesId == DashboardApiFixture.MonthlySeriesId);

        Assert.Equal(SeriesFrequency.Monthly, monthly.Frequency);
        Assert.Equal(PeriodType.Month, monthly.NativePeriodType);
        Assert.Equal("CPI — All items", monthly.CategoryName);
        Assert.NotNull(monthly.RowVersion);

        // The latest value is the current vintage of the revised month, not the superseded one.
        Assert.NotNull(monthly.Latest);
        Assert.Equal(DashboardApiFixture.RevisedMonth, monthly.Latest!.ReferenceDate);
        Assert.Equal(DashboardApiFixture.RevisedCurrentValue, monthly.Latest.Value);
    }

    [Fact]
    public async Task GetSeries_SearchMatchesTitleAndCode()
    {
        var byCode = await _client.GetJsonAsync<PagedResult<SeriesDto>>("/api/series?search=CUUR0000SA0L1E");

        Assert.Equal(DashboardApiFixture.MutableSeriesId, Assert.Single(byCode.Items).SeriesId);

        var byTitle = await _client.GetJsonAsync<PagedResult<SeriesDto>>("/api/series?search=SOFR");

        Assert.All(byTitle.Items, s => Assert.Equal(DataSource.NyFedSofrId, s.DataSourceId));
        Assert.NotEmpty(byTitle.Items);
    }

    [Fact]
    public async Task GetSeries_PagesAndClampsPageSize()
    {
        var page = await _client.GetJsonAsync<PagedResult<SeriesDto>>("/api/series?page=2&pageSize=3");

        Assert.Equal(2, page.Page);
        Assert.Equal(3, page.PageSize);
        Assert.Equal(3, page.Items.Count);
        Assert.True(page.HasPreviousPage);

        // Ten series are seeded, so page 2 of 3 is not the last.
        Assert.True(page.HasNextPage);

        var clamped = await _client.GetJsonAsync<PagedResult<SeriesDto>>("/api/series?pageSize=100000");

        Assert.Equal(PageRequest.MaxPageSize, clamped.PageSize);
    }

    [Fact]
    public async Task GetSeriesById_UnknownId_Returns404()
    {
        var response = await _client.GetAsync("/api/series/99999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateSeries_WithStaleRowVersion_Returns409()
    {
        var original = await _client.GetJsonAsync<SeriesDto>($"/api/series/{DashboardApiFixture.MutableSeriesId}");

        var staleRowVersion = original.RowVersion;

        var first = await _client.PutAsJsonAsync(
            $"/api/series/{DashboardApiFixture.MutableSeriesId}",
            new SeriesUpdateRequest
            {
                Title = "Renamed by the first caller",
                CategoryId = original.CategoryId,
                IsActive = true,
                RowVersion = staleRowVersion
            },
            DashboardApiFixture.Json);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var updated = await first.Content.ReadFromJsonAsync<SeriesDto>(DashboardApiFixture.Json);

        Assert.Equal("Renamed by the first caller", updated!.Title);
        Assert.NotEqual(staleRowVersion, updated.RowVersion);

        // The second caller read the row before the first write landed.
        var second = await _client.PutAsJsonAsync(
            $"/api/series/{DashboardApiFixture.MutableSeriesId}",
            new SeriesUpdateRequest
            {
                Title = "Renamed by the second caller",
                CategoryId = original.CategoryId,
                IsActive = true,
                RowVersion = staleRowVersion
            },
            DashboardApiFixture.Json);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var unchanged = await _client.GetJsonAsync<SeriesDto>($"/api/series/{DashboardApiFixture.MutableSeriesId}");

        Assert.Equal("Renamed by the first caller", unchanged.Title);

        await _client.PutAsJsonAsync(
            $"/api/series/{DashboardApiFixture.MutableSeriesId}",
            new SeriesUpdateRequest
            {
                Title = original.Title,
                CategoryId = original.CategoryId,
                IsActive = true,
                RowVersion = unchanged.RowVersion
            },
            DashboardApiFixture.Json);
    }

    [Fact]
    public async Task UpdateSeries_UnknownCategory_Returns400()
    {
        var response = await _client.PutAsJsonAsync(
            $"/api/series/{DashboardApiFixture.MonthlySeriesId}",
            new SeriesUpdateRequest { Title = "Whatever", CategoryId = 9999, IsActive = true },
            DashboardApiFixture.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Category_CreateReadUpdateDelete_RoundTrips()
    {
        var created = await _client.PostAsJsonAsync(
            "/api/categories",
            new SeriesCategoryCreateRequest
            {
                Code = $"api-test-{Guid.NewGuid():N}",
                DisplayName = "Created by a test",
                SortOrder = 900
            },
            DashboardApiFixture.Json);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var category = await created.Content.ReadFromJsonAsync<SeriesCategoryDto>(DashboardApiFixture.Json);

        Assert.NotNull(category);
        Assert.Equal($"/api/categories/{category!.CategoryId}", created.Headers.Location?.ToString());
        Assert.Equal(0, category.SeriesCount);

        var renamed = await _client.PutAsJsonAsync(
            $"/api/categories/{category.CategoryId}",
            new SeriesCategoryUpdateRequest { DisplayName = "Renamed", SortOrder = 901 },
            DashboardApiFixture.Json);

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        var fetched = await _client.GetJsonAsync<SeriesCategoryDto>($"/api/categories/{category.CategoryId}");

        Assert.Equal("Renamed", fetched.DisplayName);

        var deleted = await _client.DeleteAsync($"/api/categories/{category.CategoryId}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var gone = await _client.GetAsync($"/api/categories/{category.CategoryId}");

        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task CreateCategory_DuplicateCode_Returns409()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/categories",
            new SeriesCategoryCreateRequest { Code = "cpi-headline", DisplayName = "Duplicate" },
            DashboardApiFixture.Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreateCategory_UnknownParent_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/categories",
            new SeriesCategoryCreateRequest
            {
                Code = $"api-test-{Guid.NewGuid():N}",
                DisplayName = "Orphan",
                ParentCategoryId = 9999
            },
            DashboardApiFixture.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateCategory_MissingDisplayName_Returns400()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/categories",
            new SeriesCategoryCreateRequest { Code = $"api-test-{Guid.NewGuid():N}", DisplayName = "" },
            DashboardApiFixture.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DeleteCategory_WithSeries_Returns409()
    {
        // Category 1 holds the seeded headline CPI series.
        var response = await _client.DeleteAsync("/api/categories/1");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var db = _fixture.CreateContext();

        Assert.True(await db.SeriesCategories.FindAsync(1) is not null);
    }
}
