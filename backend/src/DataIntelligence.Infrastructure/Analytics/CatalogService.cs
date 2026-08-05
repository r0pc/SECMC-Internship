using System.Linq.Expressions;
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

    public async Task<IReadOnlyList<DataSourceDto>> GetSourcesAsync(CancellationToken cancellationToken) =>
        await SourceProjection(_db.DataSources.AsNoTracking().OrderBy(s => s.DataSourceId))
            .ToListAsync(cancellationToken);

    public async Task<DataSourceDto?> GetSourceAsync(byte dataSourceId, CancellationToken cancellationToken) =>
        await SourceProjection(_db.DataSources.AsNoTracking().Where(s => s.DataSourceId == dataSourceId))
            .FirstOrDefaultAsync(cancellationToken);

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

    private static IQueryable<DataSourceDto> SourceProjection(IQueryable<DataSource> query) =>
        query.Select(s => new DataSourceDto
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
            SeriesCount = s.Series.Count(x => x.IsActive)
        });

    // --------------------------------------------------------------- categories

    public async Task<IReadOnlyList<SeriesCategoryDto>> GetCategoriesAsync(CancellationToken cancellationToken) =>
        await CategoryProjection(_db.SeriesCategories.AsNoTracking()
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.DisplayName))
            .ToListAsync(cancellationToken);

    public async Task<SeriesCategoryDto?> GetCategoryAsync(int categoryId, CancellationToken cancellationToken) =>
        await CategoryProjection(_db.SeriesCategories.AsNoTracking().Where(c => c.CategoryId == categoryId))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<WriteResult<SeriesCategoryDto>> CreateCategoryAsync(
        SeriesCategoryCreateRequest request,
        CancellationToken cancellationToken)
    {
        var code = request.Code.Trim();

        if (await _db.SeriesCategories.AnyAsync(c => c.Code == code, cancellationToken))
        {
            return WriteResult<SeriesCategoryDto>.Conflict($"A category with code '{code}' already exists.");
        }

        if (request.ParentCategoryId is { } parentId
            && !await _db.SeriesCategories.AnyAsync(c => c.CategoryId == parentId, cancellationToken))
        {
            return WriteResult<SeriesCategoryDto>.InvalidReference($"No category with id {parentId}.");
        }

        var category = new SeriesCategory
        {
            Code = code,
            DisplayName = request.DisplayName.Trim(),
            ParentCategoryId = request.ParentCategoryId,
            SortOrder = request.SortOrder,
            CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime
        };

        _db.SeriesCategories.Add(category);
        await _db.SaveChangesAsync(cancellationToken);

        var created = await GetCategoryAsync(category.CategoryId, cancellationToken);
        return WriteResult<SeriesCategoryDto>.Success(created!);
    }

    public async Task<WriteResult<SeriesCategoryDto>> UpdateCategoryAsync(
        int categoryId,
        SeriesCategoryUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var category = await _db.SeriesCategories
            .FirstOrDefaultAsync(c => c.CategoryId == categoryId, cancellationToken);

        if (category is null)
        {
            return WriteResult<SeriesCategoryDto>.NotFound($"No category with id {categoryId}.");
        }

        if (request.ParentCategoryId is { } parentId)
        {
            if (parentId == categoryId)
            {
                return WriteResult<SeriesCategoryDto>.Conflict("A category cannot be its own parent.");
            }

            if (!await _db.SeriesCategories.AnyAsync(c => c.CategoryId == parentId, cancellationToken))
            {
                return WriteResult<SeriesCategoryDto>.InvalidReference($"No category with id {parentId}.");
            }

            if (await WouldCreateCycleAsync(categoryId, parentId, cancellationToken))
            {
                return WriteResult<SeriesCategoryDto>.Conflict(
                    $"Category {parentId} is a descendant of category {categoryId}; the move would "
                    + "create a cycle.");
            }
        }

        category.DisplayName = request.DisplayName.Trim();
        category.ParentCategoryId = request.ParentCategoryId;
        category.SortOrder = request.SortOrder;

        await _db.SaveChangesAsync(cancellationToken);

        var updated = await GetCategoryAsync(categoryId, cancellationToken);
        return WriteResult<SeriesCategoryDto>.Success(updated!);
    }

    public async Task<WriteResult<bool>> DeleteCategoryAsync(int categoryId, CancellationToken cancellationToken)
    {
        var category = await _db.SeriesCategories
            .FirstOrDefaultAsync(c => c.CategoryId == categoryId, cancellationToken);

        if (category is null)
        {
            return WriteResult<bool>.NotFound($"No category with id {categoryId}.");
        }

        var seriesCount = await _db.Series.CountAsync(s => s.CategoryId == categoryId, cancellationToken);

        if (seriesCount > 0)
        {
            return WriteResult<bool>.Conflict(
                $"{seriesCount} series still reference this category. Reassign them first.");
        }

        var childCount = await _db.SeriesCategories
            .CountAsync(c => c.ParentCategoryId == categoryId, cancellationToken);

        if (childCount > 0)
        {
            return WriteResult<bool>.Conflict(
                $"{childCount} child categories still reference this category. Reassign them first.");
        }

        _db.SeriesCategories.Remove(category);
        await _db.SaveChangesAsync(cancellationToken);

        return WriteResult<bool>.Success(true);
    }

    /// <summary>
    /// Walks up from the proposed parent looking for the category being moved. The self-parent
    /// check is enforced by a CHECK constraint; a longer cycle is not, and would strand a whole
    /// branch of the tree outside every dashboard that renders it from the roots down.
    /// </summary>
    private async Task<bool> WouldCreateCycleAsync(
        int categoryId,
        int proposedParentId,
        CancellationToken cancellationToken)
    {
        // The hierarchy is a handful of levels deep, so walking it beats a recursive CTE for
        // readability. The guard is against a corrupt row, not against depth.
        var parents = await _db.SeriesCategories.AsNoTracking()
            .Select(c => new { c.CategoryId, c.ParentCategoryId })
            .ToDictionaryAsync(c => c.CategoryId, c => c.ParentCategoryId, cancellationToken);

        var current = (int?)proposedParentId;
        var steps = 0;

        while (current is { } id && steps++ <= parents.Count)
        {
            if (id == categoryId)
            {
                return true;
            }

            current = parents.TryGetValue(id, out var parent) ? parent : null;
        }

        return false;
    }

    private static IQueryable<SeriesCategoryDto> CategoryProjection(IQueryable<SeriesCategory> query) =>
        query.Select(c => new SeriesCategoryDto
        {
            CategoryId = c.CategoryId,
            ParentCategoryId = c.ParentCategoryId,
            Code = c.Code,
            DisplayName = c.DisplayName,
            SortOrder = c.SortOrder,
            SeriesCount = c.Series.Count(s => s.IsActive),
            CreatedAtUtc = c.CreatedAtUtc
        });

    // ------------------------------------------------------------------- series

    public async Task<PagedResult<SeriesDto>> GetSeriesAsync(
        SeriesQuery query,
        CancellationToken cancellationToken)
    {
        var filtered = _db.Series.AsNoTracking().AsQueryable();

        if (query.DataSourceId is { } sourceId)
        {
            filtered = filtered.Where(s => s.DataSourceId == sourceId);
        }

        if (query.CategoryId is { } categoryId)
        {
            filtered = filtered.Where(s => s.CategoryId == categoryId);
        }

        if (query.Frequency is { } frequency)
        {
            filtered = filtered.Where(s => s.Frequency == frequency);
        }

        if (query.SeasonalAdjustment is { } seasonal)
        {
            filtered = filtered.Where(s => s.SeasonalAdjustment == seasonal);
        }

        if (query.IsActive is { } isActive)
        {
            filtered = filtered.Where(s => s.IsActive == isActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // The database collation is case-insensitive, so LIKE does the work and the index on
            // Title stays usable for the prefix case. ToLower() on both sides would not.
            var pattern = $"%{EscapeLike(query.Search.Trim())}%";
            filtered = filtered.Where(s =>
                EF.Functions.Like(s.Title, pattern, "\\") || EF.Functions.Like(s.SeriesCode, pattern, "\\"));
        }

        var totalCount = await filtered.CountAsync(cancellationToken);

        if (totalCount == 0)
        {
            return PagedResult<SeriesDto>.Empty(query.Page);
        }

        var rows = await filtered
            .OrderBy(s => s.DataSourceId)
            .ThenBy(s => s.SeriesId)
            .Skip(query.Page.Skip)
            .Take(query.Page.PageSize)
            .Select(ToRow)
            .ToListAsync(cancellationToken);

        var latest = query.IncludeLatest
            ? await LoadLatestPointsAsync(rows, cancellationToken)
            : [];

        var items = rows
            .Select(row => row.ToDto(latest.GetValueOrDefault(row.SeriesId)))
            .ToList();

        return PagedResult<SeriesDto>.From(items, query.Page, totalCount);
    }

    public async Task<SeriesDto?> GetSeriesByIdAsync(int seriesId, CancellationToken cancellationToken)
    {
        var row = await _db.Series.AsNoTracking()
            .Where(s => s.SeriesId == seriesId)
            .Select(ToRow)
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var latest = await LoadLatestPointsAsync([row], cancellationToken);
        return row.ToDto(latest.GetValueOrDefault(row.SeriesId));
    }

    public async Task<WriteResult<SeriesDto>> UpdateSeriesAsync(
        int seriesId,
        SeriesUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var series = await _db.Series.FirstOrDefaultAsync(s => s.SeriesId == seriesId, cancellationToken);

        if (series is null)
        {
            return WriteResult<SeriesDto>.NotFound($"No series with id {seriesId}.");
        }

        if (request.CategoryId is { } categoryId
            && !await _db.SeriesCategories.AnyAsync(c => c.CategoryId == categoryId, cancellationToken))
        {
            return WriteResult<SeriesDto>.InvalidReference($"No category with id {categoryId}.");
        }

        if (!string.IsNullOrWhiteSpace(request.RowVersion))
        {
            if (!TryDecodeRowVersion(request.RowVersion, out var rowVersion))
            {
                return WriteResult<SeriesDto>.Conflict(
                    "rowVersion is not valid base64. Re-read the series and retry with the value it returns.");
            }

            // Telling EF what we believe the row looked like turns a lost update into a 409.
            _db.Entry(series).Property(s => s.RowVersion).OriginalValue = rowVersion;
        }

        series.Title = request.Title.Trim();
        series.CategoryId = request.CategoryId;
        series.DecimalPlaces = request.DecimalPlaces ?? series.DecimalPlaces;
        series.IsActive = request.IsActive;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return WriteResult<SeriesDto>.Conflict(
                "The series was modified by someone else. Re-read it and reapply your change.");
        }

        var updated = await GetSeriesByIdAsync(seriesId, cancellationToken);
        return WriteResult<SeriesDto>.Success(updated!);
    }

    /// <summary>
    /// Newest current value per series, in one query per distinct period length.
    /// </summary>
    /// <remarks>
    /// Grouped by native period type because the filter differs per series and a single query
    /// would need a CASE over every frequency. In practice the catalogue holds two groups —
    /// monthly CPI and daily SOFR — so this is two round trips regardless of page size, rather
    /// than one per series.
    /// <para>
    /// The max-then-join shape is what makes it one query: SQL Server computes the per-series
    /// maximum in a derived table and seeks back into <c>IX_Observation_Series_Reference</c>,
    /// which already includes <c>Value</c>.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<int, SeriesLatestPointDto>> LoadLatestPointsAsync(
        IReadOnlyCollection<SeriesRow> rows,
        CancellationToken cancellationToken)
    {
        var latest = new Dictionary<int, SeriesLatestPointDto>();

        foreach (var group in rows.GroupBy(r => SeriesPeriods.NativePeriodType(r.Frequency)))
        {
            var periodType = group.Key;
            var ids = group.Select(r => r.SeriesId).ToArray();

            var candidates = _db.Observations.AsNoTracking()
                .Where(o => ids.Contains(o.SeriesId) && o.IsCurrent && o.PeriodType == periodType);

            var points = await candidates
                .Join(
                    candidates
                        .GroupBy(o => o.SeriesId)
                        .Select(g => new { SeriesId = g.Key, ReferenceDate = g.Max(o => o.ReferenceDate) }),
                    o => new { o.SeriesId, o.ReferenceDate },
                    m => new { m.SeriesId, m.ReferenceDate },
                    (o, _) => new { o.SeriesId, o.ReferenceDate, o.Value, o.CollectedAtUtc })
                .ToListAsync(cancellationToken);

            foreach (var point in points)
            {
                latest[point.SeriesId] = new SeriesLatestPointDto
                {
                    ReferenceDate = point.ReferenceDate,
                    Value = point.Value,
                    CollectedAtUtc = point.CollectedAtUtc
                };
            }
        }

        return latest;
    }

    /// <summary>Escapes the LIKE wildcards, so a search for "50%" does not match everything.</summary>
    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static bool TryDecodeRowVersion(string value, out byte[] rowVersion)
    {
        var buffer = new byte[((value.Length + 3) / 4) * 3];

        if (Convert.TryFromBase64String(value, buffer, out var written))
        {
            rowVersion = buffer[..written];
            return true;
        }

        rowVersion = [];
        return false;
    }

    /// <summary>
    /// One projection, shared by the list and the by-id read, so the two cannot drift apart.
    /// <c>Category!</c> is a left join — the null-forgiving operator satisfies the compiler and
    /// EF returns null for a series with no category, which <see cref="SeriesRow"/> allows for.
    /// </summary>
    private static readonly Expression<Func<Series, SeriesRow>> ToRow = s => new SeriesRow(
        s.SeriesId,
        s.DataSourceId,
        s.DataSource!.Code,
        s.SeriesCode,
        s.IsSourceAssignedCode,
        s.Title,
        s.CategoryId,
        s.Category!.DisplayName,
        s.Unit,
        s.DecimalPlaces,
        s.Frequency,
        s.SeasonalAdjustment,
        s.SourceUrl,
        s.IsActive,
        s.FirstSeenAtUtc,
        s.LastSeenAtUtc,
        s.RowVersion);

    /// <summary>
    /// The series columns the projection needs. A named shape rather than an anonymous type so
    /// it can be handed between methods.
    /// </summary>
    private sealed record SeriesRow(
        int SeriesId,
        byte DataSourceId,
        string SourceCode,
        string SeriesCode,
        bool IsSourceAssignedCode,
        string Title,
        int? CategoryId,
        string? CategoryName,
        string Unit,
        byte? DecimalPlaces,
        SeriesFrequency Frequency,
        SeasonalAdjustment SeasonalAdjustment,
        string? SourceUrl,
        bool IsActive,
        DateTime? FirstSeenAtUtc,
        DateTime? LastSeenAtUtc,
        byte[]? RowVersion)
    {
        public SeriesDto ToDto(SeriesLatestPointDto? latest) => new()
        {
            SeriesId = SeriesId,
            DataSourceId = DataSourceId,
            SourceCode = SourceCode,
            SeriesCode = SeriesCode,
            IsSourceAssignedCode = IsSourceAssignedCode,
            Title = Title,
            CategoryId = CategoryId,
            CategoryName = CategoryName,
            Unit = Unit,
            DecimalPlaces = DecimalPlaces,
            Frequency = Frequency,
            SeasonalAdjustment = SeasonalAdjustment,
            NativePeriodType = SeriesPeriods.NativePeriodType(Frequency),
            SourceUrl = SourceUrl,
            IsActive = IsActive,
            FirstSeenAtUtc = FirstSeenAtUtc,
            LastSeenAtUtc = LastSeenAtUtc,
            Latest = latest,
            RowVersion = RowVersion is null ? null : Convert.ToBase64String(RowVersion)
        };
    }
}
