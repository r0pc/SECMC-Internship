using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Infrastructure.Collection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataIntelligence.UnitTests.Collection;

/// <summary>
/// The request window, which is how a backfill asks for years the scheduled cycle never would.
/// </summary>
public class CollectionWindowTests
{
    private static BlsCpiAdapter Bls(int maxYearsPerRequest = 20) => new(
        Options.Create(new CollectionOptions
        {
            Bls = new BlsOptions { YearsOfHistory = 2, MaxYearsPerRequest = maxYearsPerRequest }
        }),
        NullLogger<BlsCpiAdapter>.Instance);

    private static SourceRequestContext Context(CollectionWindow? window) =>
        new(new Core.Entities.DataSource
        {
            DataSourceId = 1,
            Code = "TEST",
            ApiEndpoint = "https://example.test/api"
        },
        new DateTime(2026, 8, 5, 10, 0, 0, DateTimeKind.Utc),
        window);

    [Fact]
    public void ForYears_CoversBothEndsInclusively()
    {
        var window = CollectionWindow.ForYears(1913, 1932);

        Assert.Equal(new DateOnly(1913, 1, 1), window.From);
        Assert.Equal(new DateOnly(1932, 12, 31), window.To);
        Assert.Equal(20, window.YearSpan);
    }

    [Fact]
    public void ForYears_RejectsABackwardsRange() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CollectionWindow.ForYears(2026, 1913));

    [Fact]
    public void ASingleYearSpansOne() => Assert.Equal(1, CollectionWindow.ForYears(2026, 2026).YearSpan);

    [Fact]
    public void WithoutAWindow_BlsAsksForTheConfiguredRecentHistory()
    {
        // What the schedule does: the current and previous calendar year, which is what the
        // year-over-year comparison needs and nothing more.
        var body = Bls().BuildRequest(Context(null)).JsonBody;

        Assert.Contains("\"startyear\":\"2025\"", body);
        Assert.Contains("\"endyear\":\"2026\"", body);
    }

    [Fact]
    public void WithAWindow_BlsAsksForExactlyThoseYears()
    {
        var body = Bls().BuildRequest(Context(CollectionWindow.ForYears(1913, 1932))).JsonBody;

        Assert.Contains("\"startyear\":\"1913\"", body);
        Assert.Contains("\"endyear\":\"1932\"", body);

        // Still the one series in scope, however far back it reaches.
        Assert.Contains("\"seriesid\":[\"CUUR0000SA0\"]", body);
    }

    [Fact]
    public void AWindowWiderThanTheApiCapIsRefusedRatherThanTruncated()
    {
        // Truncating would return fewer years than asked for and leave a hole in the history
        // that nothing downstream could detect. The caller chunks; this is the assertion that it
        // did.
        var adapter = Bls(maxYearsPerRequest: 20);

        var ex = Assert.Throws<Core.Exceptions.CollectionFailureException>(
            () => adapter.BuildRequest(Context(CollectionWindow.ForYears(1913, 2026))));

        Assert.Contains("exceeds the BLS per-request cap", ex.Message);
    }

    [Fact]
    public void TheCapIsConfigurable()
    {
        // An unregistered caller is capped at ten years rather than twenty.
        var adapter = Bls(maxYearsPerRequest: 10);

        Assert.Throws<Core.Exceptions.CollectionFailureException>(
            () => adapter.BuildRequest(Context(CollectionWindow.ForYears(2000, 2019))));

        var body = adapter.BuildRequest(Context(CollectionWindow.ForYears(2010, 2019))).JsonBody;

        Assert.Contains("\"startyear\":\"2010\"", body);
    }

    [Fact]
    public void WithoutAWindow_SofrAsksForTheCurrentCalendarYear()
    {
        var url = new SofrAdapter().BuildRequest(Context(null)).Url;

        Assert.Contains("startDate=2026-01-01", url);
        Assert.Contains("endDate=2026-08-05", url);
    }

    [Fact]
    public void WithAWindow_SofrAsksForExactlyThatRange()
    {
        // The endpoint takes an arbitrary range, so unlike BLS there is nothing to chunk.
        var window = new CollectionWindow(new DateOnly(2018, 4, 3), new DateOnly(2019, 12, 31));

        var url = new SofrAdapter().BuildRequest(Context(window)).Url;

        Assert.Contains("startDate=2018-04-03", url);
        Assert.Contains("endDate=2019-12-31", url);
    }
}
