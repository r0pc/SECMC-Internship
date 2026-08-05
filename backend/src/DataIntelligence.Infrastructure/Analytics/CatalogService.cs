using DataIntelligence.Core.Analytics;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataIntelligence.Infrastructure.Analytics;

/// <inheritdoc cref="ICatalogService"/>
public sealed class CatalogService : ICatalogService
{
    private readonly DataIntelligenceDbContext _db;
    private readonly TimeProvider _timeProvider;

    public CatalogService(DataIntelligenceDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    // ------------------------------------------------------------------ sources

    public async Task<IReadOnlyList<DataSourceDto>> GetSourcesAsync(CancellationToken cancellationToken)
    {
        var sources = await _db.DataSources.AsNoTracking()
            .OrderBy(s => s.DataSourceId)
            .ToListAsync(cancellationToken);

        return sources.Select(ToDto).ToList();
    }

    public async Task<DataSourceDto?> GetSourceAsync(byte dataSourceId, CancellationToken cancellationToken)
    {
        var source = await _db.DataSources.AsNoTracking()
            .FirstOrDefaultAsync(s => s.DataSourceId == dataSourceId, cancellationToken);

        return source is null ? null : ToDto(source);
    }

    public async Task<WriteResult<DataSourceDto>> UpdateSourceAsync(
        byte dataSourceId,
        DataSourceUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var source = await _db.DataSources
            .FirstOrDefaultAsync(s => s.DataSourceId == dataSourceId, cancellationToken);

        if (source is null)
        {
            return WriteResult<DataSourceDto>.NotFound($"No data source with id {dataSourceId}.");
        }

        // Null means "leave alone", so a caller can flip one flag without having to send back
        // fields it never read.
        source.IsEnabled = request.IsEnabled ?? source.IsEnabled;
        source.CollectionIntervalMinutes = request.CollectionIntervalMinutes ?? source.CollectionIntervalMinutes;
        source.RequestTimeoutSec = request.RequestTimeoutSec ?? source.RequestTimeoutSec;
        source.MaxRetries = request.MaxRetries ?? source.MaxRetries;
        source.UserAgent = request.UserAgent ?? source.UserAgent;
        source.TermsOfUseUrl = request.TermsOfUseUrl ?? source.TermsOfUseUrl;
        source.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;

        await _db.SaveChangesAsync(cancellationToken);

        var updated = await GetSourceAsync(dataSourceId, cancellationToken);
        return WriteResult<DataSourceDto>.Success(updated!);
    }

    /// <remarks>
    /// Mapped after materialising rather than as a projection into the query, because the series
    /// count comes from the catalogue and there is no table to join to: what a source provides is
    /// fixed by the adapter compiled against it. Two rows, so the round trip is the cost either
    /// way.
    /// </remarks>
    private static DataSourceDto ToDto(DataSource s) =>
        new()
        {
            DataSourceId = s.DataSourceId,
            Code = s.Code,
            Name = s.Name,
            Publisher = s.Publisher,
            LandingPageUrl = s.LandingPageUrl,
            AccessMethod = s.AccessMethod,
            PublicationCadence = s.PublicationCadence,
            CollectionIntervalMinutes = s.CollectionIntervalMinutes,
            RequestTimeoutSec = s.RequestTimeoutSec,
            MaxRetries = s.MaxRetries,
            UserAgent = s.UserAgent,
            TermsOfUseUrl = s.TermsOfUseUrl,
            RequiresApiKey = s.RequiresApiKey,
            IsEnabled = s.IsEnabled,
            SeriesCount = SeriesCatalog.All.Count(d => d.DataSourceId == s.DataSourceId)
        };

    // ------------------------------------------------------------------- series

    public async Task<PagedResult<SeriesDto>> GetSeriesAsync(
        SeriesQuery query,
        CancellationToken cancellationToken)
    {
        var matches = SeriesCatalog.All.AsEnumerable();

        if (query.DataSourceId is { } sourceId)
        {
            matches = matches.Where(d => d.DataSourceId == sourceId);
        }

        if (query.Dataset is { } dataset)
        {
            matches = matches.Where(d => d.Dataset == dataset);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();

            matches = matches.Where(d =>
                d.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || d.Key.Contains(term, StringComparison.OrdinalIgnoreCase)
                || d.PublisherCode.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = matches.ToList();

        var page = filtered
            .Skip(query.Page.Skip)
            .Take(query.Page.PageSize)
            .ToList();

        var latest = query.IncludeLatest
            ? await LoadLatestPointsAsync(page, cancellationToken)
            : [];

        var items = page
            .Select(d => ToDto(d, latest.GetValueOrDefault(d.Key)))
            .ToList();

        return PagedResult<SeriesDto>.From(items, query.Page, filtered.Count);
    }

    public async Task<SeriesDto?> GetSeriesByKeyAsync(string seriesKey, CancellationToken cancellationToken)
    {
        if (!SeriesCatalog.TryGet(seriesKey, out var definition))
        {
            return null;
        }

        var latest = await LoadLatestPointsAsync([definition], cancellationToken);
        return ToDto(definition, latest.GetValueOrDefault(definition.Key));
    }

    /// <summary>
    /// Newest current value per series, in at most two queries regardless of how many series were
    /// asked for: one per dataset, because a dataset is one table and one row carries every
    /// measure of it.
    /// </summary>
    private async Task<Dictionary<string, SeriesLatestPointDto>> LoadLatestPointsAsync(
        IReadOnlyCollection<SeriesDefinition> definitions,
        CancellationToken cancellationToken)
    {
        var latest = new Dictionary<string, SeriesLatestPointDto>(StringComparer.OrdinalIgnoreCase);

        if (definitions.Any(d => d.Dataset == Dataset.Cpi))
        {
            // Monthly only: the newest row for the series is otherwise January's annual average,
            // which is a different number for a different period.
            var row = await _db.CpiObservations.AsNoTracking()
                .Where(o => o.IsCurrent && o.PeriodType == PeriodType.Month)
                .OrderByDescending(o => o.ReferenceDate)
                .Select(o => new SeriesLatestPointDto
                {
                    ReferenceDate = o.ReferenceDate,
                    Value = o.IndexValue,
                    CollectedAtUtc = o.CollectedAtUtc
                })
                .FirstOrDefaultAsync(cancellationToken);

            if (row is not null)
            {
                latest[SeriesCatalog.CpiKey] = row;
            }
        }

        var sofrKeys = definitions.Where(d => d.Dataset == Dataset.Sofr).ToList();

        if (sofrKeys.Count > 0)
        {
            var day = await _db.SofrDailyRates.AsNoTracking()
                .Where(r => r.IsCurrent)
                .OrderByDescending(r => r.EffectiveDate)
                .FirstOrDefaultAsync(cancellationToken);

            if (day is not null)
            {
                foreach (var definition in sofrKeys)
                {
                    // A measure absent on that day — a percentile the publisher omitted — leaves
                    // the series without a latest value rather than borrowing an earlier one,
                    // which would misdate it.
                    if (MeasureQueries.Read(day, definition.Measure) is { } value)
                    {
                        latest[definition.Key] = new SeriesLatestPointDto
                        {
                            ReferenceDate = day.EffectiveDate,
                            Value = value,
                            CollectedAtUtc = day.CollectedAtUtc
                        };
                    }
                }
            }
        }

        return latest;
    }

    private static SeriesDto ToDto(SeriesDefinition definition, SeriesLatestPointDto? latest) => new()
    {
        SeriesKey = definition.Key,
        Dataset = definition.Dataset,
        DataSourceId = definition.DataSourceId,
        SourceCode = definition.SourceCode,
        PublisherCode = definition.PublisherCode,
        Title = definition.Title,
        Unit = definition.Unit,
        DecimalPlaces = definition.DecimalPlaces,
        Frequency = definition.Frequency,
        SeasonalAdjustment = definition.SeasonalAdjustment,
        SourceUrl = definition.SourceUrl,
        Latest = latest
    };
}
