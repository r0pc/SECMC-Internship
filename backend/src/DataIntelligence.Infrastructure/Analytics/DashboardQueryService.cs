using DataIntelligence.Core.Analytics;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DataIntelligence.Infrastructure.Analytics;

/// <inheritdoc cref="IDashboardQueryService"/>
public sealed class DashboardQueryService : IDashboardQueryService
{
    /// <summary>
    /// How far back the consecutive-failure counter looks. A source that has failed twenty runs
    /// in a row is broken; the exact number past that changes no decision, and the cap keeps the
    /// query a bounded index seek.
    /// </summary>
    private const int ConsecutiveFailureScanLimit = 20;

    private readonly DataIntelligenceDbContext _db;
    private readonly TimeProvider _timeProvider;

    public DashboardQueryService(DataIntelligenceDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------- observations

    public async Task<PagedResult<ObservationDto>?> GetObservationsAsync(
        ObservationQuery query,
        CancellationToken cancellationToken)
    {
        var series = await _db.Series.AsNoTracking()
            .Where(s => s.SeriesId == query.SeriesId)
            .Select(s => new { s.SeriesId, s.Frequency })
            .FirstOrDefaultAsync(cancellationToken);

        if (series is null)
        {
            return null;
        }

        var periodType = query.PeriodType ?? SeriesPeriods.NativePeriodType(series.Frequency);

        var filtered = _db.Observations.AsNoTracking()
            .Where(o => o.SeriesId == query.SeriesId && o.PeriodType == periodType);

        if (query.From is { } from)
        {
            filtered = filtered.Where(o => o.ReferenceDate >= from);
        }

        if (query.To is { } to)
        {
            filtered = filtered.Where(o => o.ReferenceDate <= to);
        }

        if (query.AsOfUtc is { } asOf)
        {
            // The vintage in force at that instant: collected by then, and not yet superseded.
            // This selects exactly one row per period on its own, so IncludeRevisions has nothing
            // left to add and is ignored.
            filtered = filtered.Where(o =>
                o.CollectedAtUtc <= asOf && (o.SupersededAtUtc == null || o.SupersededAtUtc > asOf));
        }
        else if (!query.IncludeRevisions)
        {
            filtered = filtered.Where(o => o.IsCurrent);
        }

        var totalCount = await filtered.CountAsync(cancellationToken);

        if (totalCount == 0)
        {
            return PagedResult<ObservationDto>.Empty(query.Page);
        }

        var ordered = query.Sort == SortDirection.Descending
            ? filtered.OrderByDescending(o => o.ReferenceDate).ThenByDescending(o => o.RevisionNumber)
            : filtered.OrderBy(o => o.ReferenceDate).ThenBy(o => o.RevisionNumber);

        var items = await ordered
            .Skip(query.Page.Skip)
            .Take(query.Page.PageSize)
            .Select(o => new ObservationDto
            {
                ObservationId = o.ObservationId,
                SeriesId = o.SeriesId,
                ReferenceDate = o.ReferenceDate,
                PeriodType = o.PeriodType,
                SourcePeriodCode = o.SourcePeriodCode,
                Value = o.Value,
                RevisionNumber = o.RevisionNumber,
                IsCurrent = o.IsCurrent,
                SupersededAtUtc = o.SupersededAtUtc,
                SourceAnnotation = o.SourceAnnotation,
                CollectedAtUtc = o.CollectedAtUtc,
                CollectionRunId = o.CollectionRunId
            })
            .ToListAsync(cancellationToken);

        return PagedResult<ObservationDto>.From(items, query.Page, totalCount);
    }

    // ------------------------------------------------------------------- trends

    public async Task<IReadOnlyList<TrendSeriesDto>> GetTrendAsync(
        TrendQuery query,
        CancellationToken cancellationToken)
    {
        var requestedIds = query.SeriesIds.Distinct().ToArray();

        if (requestedIds.Length == 0)
        {
            return [];
        }

        var series = await _db.Series.AsNoTracking()
            .Where(s => requestedIds.Contains(s.SeriesId))
            .Select(s => new TrendSeriesInfo(
                s.SeriesId, s.SeriesCode, s.Title, s.Unit, s.DecimalPlaces, s.Frequency))
            .ToListAsync(cancellationToken);

        if (series.Count == 0)
        {
            return [];
        }

        var (from, to) = ResolveRange(query.From, query.To);

        // The densest series decides the bucket: a chart mixing monthly CPI with daily SOFR must
        // not be bucketed as if everything were monthly, or the SOFR line silently loses detail
        // the caller did not ask to lose.
        var densest = series
            .OrderByDescending(s => SeriesPeriods.ReleasesPerYear(s.Frequency))
            .First()
            .Frequency;

        var granularity = SeriesPeriods.ResolveGranularity(query.Granularity, densest, from, to);

        var points = granularity == TrendGranularity.Point
            ? await LoadPointsAsync(series, from, to, cancellationToken)
            : await LoadBucketsAsync(series, from, to, granularity, cancellationToken);

        // Requested order, so the frontend can pair a colour with a series without re-sorting.
        return requestedIds
            .Select(id => series.FirstOrDefault(s => s.SeriesId == id))
            .Where(s => s is not null)
            .Select(s => new TrendSeriesDto
            {
                SeriesId = s!.SeriesId,
                SeriesCode = s.SeriesCode,
                Title = s.Title,
                Unit = s.Unit,
                DecimalPlaces = s.DecimalPlaces,
                Granularity = granularity,
                Points = points.GetValueOrDefault(s.SeriesId, [])
            })
            .ToList();
    }

    /// <summary>One point per observation, for ranges short enough not to need bucketing.</summary>
    private async Task<Dictionary<int, IReadOnlyList<TrendPointDto>>> LoadPointsAsync(
        IReadOnlyCollection<TrendSeriesInfo> series,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, IReadOnlyList<TrendPointDto>>();

        foreach (var group in series.GroupBy(s => SeriesPeriods.NativePeriodType(s.Frequency)))
        {
            var periodType = group.Key;
            var ids = group.Select(s => s.SeriesId).ToArray();

            var rows = await _db.Observations.AsNoTracking()
                .Where(o => ids.Contains(o.SeriesId)
                    && o.IsCurrent
                    && o.PeriodType == periodType
                    && o.ReferenceDate >= from
                    && o.ReferenceDate <= to)
                .OrderBy(o => o.SeriesId)
                .ThenBy(o => o.ReferenceDate)
                .Select(o => new { o.SeriesId, o.ReferenceDate, o.Value })
                .ToListAsync(cancellationToken);

            foreach (var seriesRows in rows.GroupBy(r => r.SeriesId))
            {
                result[seriesRows.Key] = seriesRows
                    .Select(r => new TrendPointDto
                    {
                        BucketStart = r.ReferenceDate,
                        BucketEnd = r.ReferenceDate,
                        Value = r.Value,
                        Minimum = r.Value,
                        Maximum = r.Value,
                        ObservationCount = 1
                    })
                    .ToList();
            }
        }

        return result;
    }

    /// <summary>
    /// Aggregated buckets, computed by SQL Server.
    /// </summary>
    /// <remarks>
    /// Grouped on <c>DATEPART</c> integers rather than a constructed date, because building a
    /// <see cref="DateOnly"/> inside a query is not translatable; the parts come back and become
    /// a date in <see cref="SeriesPeriods.BucketStartFromParts"/>.
    /// </remarks>
    private async Task<Dictionary<int, IReadOnlyList<TrendPointDto>>> LoadBucketsAsync(
        IReadOnlyCollection<TrendSeriesInfo> series,
        DateOnly from,
        DateOnly to,
        TrendGranularity granularity,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, IReadOnlyList<TrendPointDto>>();

        foreach (var group in series.GroupBy(s => SeriesPeriods.NativePeriodType(s.Frequency)))
        {
            var periodType = group.Key;
            var ids = group.Select(s => s.SeriesId).ToArray();

            var scope = _db.Observations.AsNoTracking()
                .Where(o => ids.Contains(o.SeriesId)
                    && o.IsCurrent
                    && o.PeriodType == periodType
                    && o.ReferenceDate >= from
                    && o.ReferenceDate <= to);

            var rows = granularity switch
            {
                TrendGranularity.Month => await scope
                    .GroupBy(o => new { o.SeriesId, o.ReferenceDate.Year, Ordinal = o.ReferenceDate.Month })
                    .Select(g => new BucketRow(
                        g.Key.SeriesId,
                        g.Key.Year,
                        g.Key.Ordinal,
                        g.Average(o => o.Value),
                        g.Min(o => o.Value),
                        g.Max(o => o.Value),
                        g.Count()))
                    .ToListAsync(cancellationToken),

                TrendGranularity.Quarter => await scope
                    .GroupBy(o => new
                    {
                        o.SeriesId,
                        o.ReferenceDate.Year,
                        Ordinal = ((o.ReferenceDate.Month - 1) / 3) + 1
                    })
                    .Select(g => new BucketRow(
                        g.Key.SeriesId,
                        g.Key.Year,
                        g.Key.Ordinal,
                        g.Average(o => o.Value),
                        g.Min(o => o.Value),
                        g.Max(o => o.Value),
                        g.Count()))
                    .ToListAsync(cancellationToken),

                _ => await scope
                    .GroupBy(o => new { o.SeriesId, o.ReferenceDate.Year })
                    .Select(g => new BucketRow(
                        g.Key.SeriesId,
                        g.Key.Year,
                        1,
                        g.Average(o => o.Value),
                        g.Min(o => o.Value),
                        g.Max(o => o.Value),
                        g.Count()))
                    .ToListAsync(cancellationToken)
            };

            foreach (var seriesRows in rows.GroupBy(r => r.SeriesId))
            {
                result[seriesRows.Key] = seriesRows
                    .Select(r =>
                    {
                        var start = SeriesPeriods.BucketStartFromParts(r.Year, r.Ordinal, granularity);

                        return new TrendPointDto
                        {
                            BucketStart = start,
                            BucketEnd = SeriesPeriods.BucketEnd(start, granularity),
                            Value = r.Average,
                            Minimum = r.Minimum,
                            Maximum = r.Maximum,
                            ObservationCount = r.ObservationCount
                        };
                    })
                    .OrderBy(p => p.BucketStart)
                    .ToList();
            }
        }

        return result;
    }

    // --------------------------------------------------------------------- KPIs

    public async Task<IReadOnlyList<SeriesKpiDto>> GetKpisAsync(
        IReadOnlyList<int> seriesIds,
        CancellationToken cancellationToken)
    {
        var requestedIds = seriesIds.Distinct().ToArray();

        if (requestedIds.Length == 0)
        {
            return [];
        }

        var series = await _db.Series.AsNoTracking()
            .Where(s => requestedIds.Contains(s.SeriesId))
            .Select(s => new
            {
                s.SeriesId,
                s.SeriesCode,
                s.Title,
                s.Unit,
                s.DecimalPlaces,
                s.Frequency,
                s.SeasonalAdjustment
            })
            .ToListAsync(cancellationToken);

        var kpis = new List<SeriesKpiDto>(series.Count);

        // Per series rather than one query across all of them: the period filter differs by
        // frequency, and each of these is a two-row seek on IX_Observation_Series_Reference. The
        // endpoint caps how many series a request may ask for, which bounds the round trips.
        foreach (var id in requestedIds)
        {
            var info = series.FirstOrDefault(s => s.SeriesId == id);

            if (info is null)
            {
                continue;
            }

            var periodType = SeriesPeriods.NativePeriodType(info.Frequency);

            var current = _db.Observations.AsNoTracking()
                .Where(o => o.SeriesId == id && o.IsCurrent && o.PeriodType == periodType);

            var recent = await current
                .OrderByDescending(o => o.ReferenceDate)
                .Take(2)
                .Select(o => new { o.ReferenceDate, o.Value, o.CollectedAtUtc })
                .ToListAsync(cancellationToken);

            var latest = recent.FirstOrDefault();
            var previous = recent.Skip(1).FirstOrDefault();

            // Computed here, not inside the predicate, so the comparison reaches SQL Server as a
            // parameter rather than as a date function it has to evaluate per row.
            var yearAgoCutoff = latest?.ReferenceDate.AddYears(-1);

            var yearAgo = yearAgoCutoff is null
                ? null
                : await current
                    .Where(o => o.ReferenceDate <= yearAgoCutoff.Value)
                    .OrderByDescending(o => o.ReferenceDate)
                    .Select(o => new { o.ReferenceDate, o.Value })
                    .FirstOrDefaultAsync(cancellationToken);

            kpis.Add(new SeriesKpiDto
            {
                SeriesId = info.SeriesId,
                SeriesCode = info.SeriesCode,
                Title = info.Title,
                Unit = info.Unit,
                DecimalPlaces = info.DecimalPlaces,
                Frequency = info.Frequency,
                SeasonalAdjustment = info.SeasonalAdjustment,
                Latest = latest is null
                    ? null
                    : new SeriesLatestPointDto
                    {
                        ReferenceDate = latest.ReferenceDate,
                        Value = latest.Value,
                        CollectedAtUtc = latest.CollectedAtUtc
                    },
                PreviousValue = previous?.Value,
                PreviousReferenceDate = previous?.ReferenceDate,
                ChangeFromPrevious = latest is null || previous is null
                    ? null
                    : latest.Value - previous.Value,
                PercentChangeFromPrevious = latest is null || previous is null
                    ? null
                    : SeriesPeriods.PercentChange(latest.Value, previous.Value),
                YearAgoValue = yearAgo?.Value,
                YearAgoReferenceDate = yearAgo?.ReferenceDate,
                ChangeFromYearAgo = latest is null || yearAgo is null
                    ? null
                    : latest.Value - yearAgo.Value,
                PercentChangeFromYearAgo = latest is null || yearAgo is null
                    ? null
                    : SeriesPeriods.PercentChange(latest.Value, yearAgo.Value)
            });
        }

        return kpis;
    }

    // ------------------------------------------------------------------ summary

    public async Task<DashboardSummaryDto> GetSummaryAsync(
        int windowDays,
        CancellationToken cancellationToken)
    {
        var sourceCount = await _db.DataSources.CountAsync(cancellationToken);
        var activeSeriesCount = await _db.Series.CountAsync(s => s.IsActive, cancellationToken);
        var categoryCount = await _db.SeriesCategories.CountAsync(cancellationToken);

        var currentObservations = _db.Observations.AsNoTracking().Where(o => o.IsCurrent);

        var observationCount = await currentObservations.LongCountAsync(cancellationToken);

        // Nullable projections so an empty table returns null instead of throwing on Min/Max.
        var earliest = await currentObservations
            .Select(o => (DateOnly?)o.ReferenceDate)
            .MinAsync(cancellationToken);

        var latest = await currentObservations
            .Select(o => (DateOnly?)o.ReferenceDate)
            .MaxAsync(cancellationToken);

        var lastCollection = await _db.CollectionRuns.AsNoTracking()
            .Where(r => r.Status == CollectionRunStatus.Succeeded
                || r.Status == CollectionRunStatus.PartialSuccess)
            .Select(r => (DateTime?)r.StartedAtUtc)
            .MaxAsync(cancellationToken);

        return new DashboardSummaryDto
        {
            SourceCount = sourceCount,
            ActiveSeriesCount = activeSeriesCount,
            CategoryCount = categoryCount,
            ObservationCount = observationCount,
            EarliestReferenceDate = earliest,
            LatestReferenceDate = latest,
            LastCollectionAtUtc = lastCollection,
            Sources = await GetHealthAsync(windowDays, cancellationToken)
        };
    }

    // ------------------------------------------------------- collection health

    public async Task<IReadOnlyList<SourceHealthDto>> GetHealthAsync(
        int windowDays,
        CancellationToken cancellationToken)
    {
        var since = UtcNow.AddDays(-windowDays);

        var sources = await _db.DataSources.AsNoTracking()
            .OrderBy(s => s.DataSourceId)
            .Select(s => new { s.DataSourceId, s.Code, s.Name, s.IsEnabled })
            .ToListAsync(cancellationToken);

        var windowStats = await _db.CollectionRuns.AsNoTracking()
            .Where(r => r.StartedAtUtc >= since)
            .GroupBy(r => r.DataSourceId)
            .Select(g => new
            {
                DataSourceId = g.Key,
                Total = g.Count(),
                Succeeded = g.Count(r => r.Status == CollectionRunStatus.Succeeded),
                Partial = g.Count(r => r.Status == CollectionRunStatus.PartialSuccess),
                Failed = g.Count(r => r.Status == CollectionRunStatus.Failed)
            })
            .ToListAsync(cancellationToken);

        var health = new List<SourceHealthDto>(sources.Count);

        foreach (var source in sources)
        {
            var stats = windowStats.FirstOrDefault(s => s.DataSourceId == source.DataSourceId);

            var lastRun = await _db.CollectionRuns.AsNoTracking()
                .Where(r => r.DataSourceId == source.DataSourceId)
                .OrderByDescending(r => r.StartedAtUtc)
                .Select(r => new
                {
                    r.StartedAtUtc,
                    r.Status,
                    r.FailureCategory,
                    r.ErrorMessage
                })
                .FirstOrDefaultAsync(cancellationToken);

            var lastSuccess = await _db.CollectionRuns.AsNoTracking()
                .Where(r => r.DataSourceId == source.DataSourceId
                    && (r.Status == CollectionRunStatus.Succeeded
                        || r.Status == CollectionRunStatus.PartialSuccess))
                .OrderByDescending(r => r.StartedAtUtc)
                .Select(r => (DateTime?)r.StartedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);

            var recentStatuses = await _db.CollectionRuns.AsNoTracking()
                .Where(r => r.DataSourceId == source.DataSourceId
                    && r.Status != CollectionRunStatus.Running)
                .OrderByDescending(r => r.StartedAtUtc)
                .Take(ConsecutiveFailureScanLimit)
                .Select(r => r.Status)
                .ToListAsync(cancellationToken);

            health.Add(new SourceHealthDto
            {
                DataSourceId = source.DataSourceId,
                SourceCode = source.Code,
                Name = source.Name,
                IsEnabled = source.IsEnabled,
                WindowDays = windowDays,
                TotalRuns = stats?.Total ?? 0,
                SucceededRuns = stats?.Succeeded ?? 0,
                PartialRuns = stats?.Partial ?? 0,
                FailedRuns = stats?.Failed ?? 0,
                SuccessRatePercent = SuccessRate(stats?.Succeeded ?? 0, stats?.Partial ?? 0, stats?.Failed ?? 0),
                LastRunAtUtc = lastRun?.StartedAtUtc,
                LastSuccessAtUtc = lastSuccess,
                LastRunStatus = lastRun?.Status,
                LastFailureCategory = lastRun?.FailureCategory,
                LastErrorMessage = lastRun?.ErrorMessage,
                ConsecutiveFailures = recentStatuses
                    .TakeWhile(s => s == CollectionRunStatus.Failed)
                    .Count()
            });
        }

        return health;
    }

    /// <summary>
    /// Succeeded plus partial over completed attempts.
    /// </summary>
    /// <remarks>
    /// The denominator excludes <c>Running</c> and <c>Skipped</c> deliberately. A run still in
    /// flight has no outcome yet, and a skipped cycle — a disabled source — was never an attempt;
    /// counting either would let the headline number drift away from what the ≥99% target in the
    /// SOW actually measures. Null for an empty window, because "no runs" is not "100%".
    /// </remarks>
    private static decimal? SuccessRate(int succeeded, int partial, int failed)
    {
        var completed = succeeded + partial + failed;

        return completed == 0
            ? null
            : Math.Round((succeeded + partial) / (decimal)completed * 100m, 2);
    }

    // -------------------------------------------------------------- run history

    public async Task<PagedResult<CollectionRunDto>> GetCollectionRunsAsync(
        CollectionRunQuery query,
        CancellationToken cancellationToken)
    {
        var filtered = _db.CollectionRuns.AsNoTracking().AsQueryable();

        if (query.DataSourceId is { } sourceId)
        {
            filtered = filtered.Where(r => r.DataSourceId == sourceId);
        }

        if (query.Status is { } status)
        {
            filtered = filtered.Where(r => r.Status == status);
        }

        if (query.FailuresOnly)
        {
            filtered = filtered.Where(r => r.Status == CollectionRunStatus.Failed
                || r.Status == CollectionRunStatus.PartialSuccess);
        }

        if (query.FromUtc is { } fromUtc)
        {
            filtered = filtered.Where(r => r.StartedAtUtc >= fromUtc);
        }

        if (query.ToUtc is { } toUtc)
        {
            filtered = filtered.Where(r => r.StartedAtUtc <= toUtc);
        }

        var totalCount = await filtered.CountAsync(cancellationToken);

        if (totalCount == 0)
        {
            return PagedResult<CollectionRunDto>.Empty(query.Page);
        }

        var items = await RunProjection(filtered.OrderByDescending(r => r.StartedAtUtc))
            .Skip(query.Page.Skip)
            .Take(query.Page.PageSize)
            .ToListAsync(cancellationToken);

        return PagedResult<CollectionRunDto>.From(items, query.Page, totalCount);
    }

    public async Task<CollectionRunDto?> GetCollectionRunAsync(
        long collectionRunId,
        CancellationToken cancellationToken) =>
        await RunProjection(_db.CollectionRuns.AsNoTracking()
                .Where(r => r.CollectionRunId == collectionRunId))
            .FirstOrDefaultAsync(cancellationToken);

    private static IQueryable<CollectionRunDto> RunProjection(IQueryable<CollectionRun> query) =>
        query.Select(r => new CollectionRunDto
        {
            CollectionRunId = r.CollectionRunId,
            DataSourceId = r.DataSourceId,
            SourceCode = r.DataSource!.Code,
            ScheduledForUtc = r.ScheduledForUtc,
            Attempt = r.Attempt,
            TriggerType = r.TriggerType,
            StartedAtUtc = r.StartedAtUtc,
            CompletedAtUtc = r.CompletedAtUtc,
            DurationMs = r.DurationMs,
            Status = r.Status,
            HttpStatusCode = r.HttpStatusCode,
            ObservationsFetched = r.ObservationsFetched,
            ObservationsInserted = r.ObservationsInserted,
            ObservationsRevised = r.ObservationsRevised,
            ObservationsUnchanged = r.ObservationsUnchanged,
            ObservationsRejected = r.ObservationsRejected,
            FailureCategory = r.FailureCategory,
            ErrorMessage = r.ErrorMessage
        });

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Fills in a missing range: twelve months back from today, which is the span the
    /// performance target is written against (NFR Performance) and the one a dashboard opens on.
    /// </summary>
    private (DateOnly From, DateOnly To) ResolveRange(DateOnly? from, DateOnly? to)
    {
        var resolvedTo = to ?? DateOnly.FromDateTime(UtcNow);
        var resolvedFrom = from ?? resolvedTo.AddYears(-1);

        return (resolvedFrom, resolvedTo);
    }

    private sealed record TrendSeriesInfo(
        int SeriesId,
        string SeriesCode,
        string Title,
        string Unit,
        byte? DecimalPlaces,
        SeriesFrequency Frequency);

    /// <summary>One aggregated bucket as SQL Server returns it, before the date is rebuilt.</summary>
    private sealed record BucketRow(
        int SeriesId,
        int Year,
        int Ordinal,
        decimal Average,
        decimal Minimum,
        decimal Maximum,
        int ObservationCount);
}
