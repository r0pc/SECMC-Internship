using DataIntelligence.Core.Dtos;

namespace DataIntelligence.Core.Interfaces;

/// <summary>
/// Reads and edits the catalogue the dashboards navigate: sources, categories, and series
/// (FR-7, FR-11).
/// </summary>
/// <remarks>
/// The write surface is deliberately narrow. Sources and series are reference data that
/// describe a publisher's output, so only the fields the platform owns — presentation,
/// grouping, and polling behaviour — can be edited. Categories are the platform's own
/// invention and are fully CRUD.
/// <para>
/// Observations have no write surface at all. They are append-only by requirement (FR-4) and
/// are written solely by the collector, which is what makes the historical record trustworthy;
/// an API that could edit them would undo that in one call.
/// </para>
/// </remarks>
public interface ICatalogService
{
    Task<IReadOnlyList<DataSourceDto>> GetSourcesAsync(CancellationToken cancellationToken);

    Task<DataSourceDto?> GetSourceAsync(byte dataSourceId, CancellationToken cancellationToken);

    Task<WriteResult<DataSourceDto>> UpdateSourceAsync(
        byte dataSourceId,
        DataSourceUpdateRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SeriesCategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken);

    Task<SeriesCategoryDto?> GetCategoryAsync(int categoryId, CancellationToken cancellationToken);

    Task<WriteResult<SeriesCategoryDto>> CreateCategoryAsync(
        SeriesCategoryCreateRequest request,
        CancellationToken cancellationToken);

    Task<WriteResult<SeriesCategoryDto>> UpdateCategoryAsync(
        int categoryId,
        SeriesCategoryUpdateRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes a category. Refused while series or child categories still point at it — the
    /// alternative is orphaning them or cascading a delete through data the user cannot see.
    /// </summary>
    Task<WriteResult<bool>> DeleteCategoryAsync(int categoryId, CancellationToken cancellationToken);

    Task<PagedResult<SeriesDto>> GetSeriesAsync(SeriesQuery query, CancellationToken cancellationToken);

    Task<SeriesDto?> GetSeriesByIdAsync(int seriesId, CancellationToken cancellationToken);

    Task<WriteResult<SeriesDto>> UpdateSeriesAsync(
        int seriesId,
        SeriesUpdateRequest request,
        CancellationToken cancellationToken);
}
