using DataIntelligence.Core.Dtos;

namespace DataIntelligence.Core.Interfaces;

/// <summary>
/// The analytical read side: observations, trends, KPIs, and collection health (FR-7, FR-10).
/// </summary>
/// <remarks>
/// Every aggregate here is computed in SQL Server rather than by pulling rows into the API and
/// looping. That is what keeps a 12-month range inside the 3-second budget (NFR Performance)
/// and what lets the existing indexes do the work.
/// </remarks>
public interface IDashboardQueryService
{
    /// <summary>
    /// One series' stored rows, paged. Returns null when the series key is unknown, which the
    /// endpoint turns into a 404 — distinguishable from a series that exists but has no data in
    /// the requested range.
    /// </summary>
    Task<PagedResult<ObservationDto>?> GetObservationsAsync(
        ObservationQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Trend lines for the requested series. Unknown keys are dropped rather than failing the
    /// request, so one stale bookmark cannot blank a whole dashboard.
    /// </summary>
    Task<IReadOnlyList<TrendSeriesDto>> GetTrendAsync(
        TrendQuery query,
        CancellationToken cancellationToken);

    /// <summary>Headline numbers per series, in the order requested.</summary>
    Task<IReadOnlyList<SeriesKpiDto>> GetKpisAsync(
        IReadOnlyList<string> seriesKeys,
        CancellationToken cancellationToken);

    Task<DashboardSummaryDto> GetSummaryAsync(int windowDays, CancellationToken cancellationToken);

    Task<PagedResult<CollectionRunDto>> GetCollectionRunsAsync(
        CollectionRunQuery query,
        CancellationToken cancellationToken);

    Task<CollectionRunDto?> GetCollectionRunAsync(
        long collectionRunId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SourceHealthDto>> GetHealthAsync(
        int windowDays,
        CancellationToken cancellationToken);
}
