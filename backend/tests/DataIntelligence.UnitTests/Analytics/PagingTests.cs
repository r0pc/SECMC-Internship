using DataIntelligence.Core.Dtos;

namespace DataIntelligence.UnitTests.Analytics;

/// <summary>Paging behaves the same on every list endpoint, so it is worth pinning down once.</summary>
public class PagingTests
{
    [Fact]
    public void Normalize_AppliesDefaults()
    {
        var page = PageRequest.Normalize(null, null);

        Assert.Equal(1, page.Page);
        Assert.Equal(PageRequest.DefaultPageSize, page.PageSize);
        Assert.Equal(0, page.Skip);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Normalize_ClampsAPageBelowOne(int requested) =>
        Assert.Equal(1, PageRequest.Normalize(requested, null).Page);

    [Fact]
    public void Normalize_ClampsAnOversizedPageRatherThanRejectingIt()
    {
        // Clamped, not refused: an over-large pageSize is a caller bug, and failing the request
        // turns a slow page into a blank one.
        var page = PageRequest.Normalize(1, 100_000);

        Assert.Equal(PageRequest.MaxPageSize, page.PageSize);
    }

    [Fact]
    public void Normalize_HonoursAHigherCeilingForObservations()
    {
        var page = PageRequest.Normalize(1, 100_000, PageRequest.ObservationPageSizeLimit);

        Assert.Equal(PageRequest.ObservationPageSizeLimit, page.PageSize);
        Assert.True(page.PageSize > PageRequest.MaxPageSize);
    }

    [Fact]
    public void Skip_CountsWholePagesBeforeTheRequestedOne()
    {
        var page = PageRequest.Normalize(4, 25);

        Assert.Equal(75, page.Skip);
    }

    [Fact]
    public void PagedResult_DerivesPagerState()
    {
        var page = PageRequest.Normalize(2, 10);
        var result = PagedResult<int>.From([1, 2, 3], page, totalCount: 23);

        Assert.Equal(3, result.TotalPages);
        Assert.True(result.HasPreviousPage);
        Assert.True(result.HasNextPage);
    }

    [Fact]
    public void PagedResult_OnTheLastPage_HasNoNext()
    {
        var result = PagedResult<int>.From([1, 2, 3], PageRequest.Normalize(3, 10), totalCount: 23);

        Assert.False(result.HasNextPage);
        Assert.True(result.HasPreviousPage);
    }

    [Fact]
    public void PagedResult_Empty_ReportsNoPages()
    {
        var result = PagedResult<int>.Empty(PageRequest.Normalize(1, 10));

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalPages);
        Assert.False(result.HasNextPage);
        Assert.False(result.HasPreviousPage);
    }
}
