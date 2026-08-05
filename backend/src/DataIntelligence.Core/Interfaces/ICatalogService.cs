using DataIntelligence.Core.Dtos;

namespace DataIntelligence.Core.Interfaces;

/// <summary>
/// Reads the catalogue the dashboards navigate: the two sources, and the measures they publish
/// (FR-7, FR-11).
/// </summary>
/// <remarks>
/// The write surface is one method. Sources describe a publisher's output, so only the fields the
/// platform owns — polling behaviour and compliance metadata — can be edited.
/// <para>
/// Series have no write surface at all any more. Each dataset is its own table with its series
/// pinned by a CHECK constraint, so what exists is a fact about the schema; presentation comes
/// from <c>SeriesCatalog</c> in code. There is nothing left to edit that would not simply make
/// the platform disagree with itself.
/// </para>
/// <para>
/// Nor do observations, for the older and better reason: they are append-only by requirement
/// (FR-4) and written solely by the collector, which is what makes the historical record
/// trustworthy. An API that could edit them would undo that in one call.
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

    Task<PagedResult<SeriesDto>> GetSeriesAsync(SeriesQuery query, CancellationToken cancellationToken);

    /// <summary>Null when no series is registered under that key, which the endpoint turns into a 404.</summary>
    Task<SeriesDto?> GetSeriesByKeyAsync(string seriesKey, CancellationToken cancellationToken);
}
