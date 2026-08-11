using DataIntelligence.Core;
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

    private DateTime UtcNow => PakistanTime.Now(_timeProvider);

    // ------------------------------------------------------------- observations

    public async Task<PagedResult<ObservationDto>?> GetObservationsAsync(
        ObservationQuery query,
        CancellationToken cancellationToken)
    {
        if (!SeriesCatalog.TryGet(query.SeriesKey, out var definition))
        {
            return null;
        }

        var filtered = MeasureQueries.Rows(_db, definition, query.PeriodType);

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
                o.CollectedAtPkt <= asOf && (o.SupersededAtPkt == null || o.SupersededAtPkt > asOf));
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

        var rows = await ordered
            .Skip(query.Page.Skip)
            .Take(query.Page.PageSize)
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(o => new ObservationDto
            {
                ObservationId = o.ObservationId,
                SeriesKey = definition.Key,
                ReferenceDate = o.ReferenceDate,
                PeriodType = o.PeriodType,
                PeriodCode = o.PeriodCode,
                Value = o.Value,
                RevisionNumber = o.RevisionNumber,
                IsCurrent = o.IsCurrent,
                SupersededAtPkt = o.SupersededAtPkt,
                SourceAnnotation = o.Annotation,
                CollectedAtPkt = o.CollectedAtPkt,
                CollectionRunId = o.CollectionRunId
            })
            .ToList();

        return PagedResult<ObservationDto>.From(items, query.Page, totalCount);
    }

    // ------------------------------------------------------------------- trends

    public async Task<IReadOnlyList<TrendSeriesDto>> GetTrendAsync(
        TrendQuery query,
        CancellationToken cancellationToken)
    {
        var requested = Resolve(query.SeriesKeys);

        if (requested.Count == 0)
        {
            return [];
        }

        var (from, to) = ResolveRange(query.From, query.To);

        // The densest series decides the bucket: a chart mixing monthly CPI with daily SOFR must
        // not be bucketed as if everything were monthly, or the SOFR line silently loses detail
        // the caller did not ask to lose.
        var densest = requested
            .OrderByDescending(d => SeriesPeriods.ReleasesPerYear(d.Frequency))
            .First()
            .Frequency;

        var granularity = SeriesPeriods.ResolveGranularity(query.Granularity, densest, from, to);

        var lines = new List<TrendSeriesDto>(requested.Count);

        // One query per line. Each is an indexed range scan over its own table, and the endpoint
        // caps how many lines a request may ask for, which bounds the round trips.
        foreach (var definition in requested)
        {
            var points = granularity == TrendGranularity.Point
                ? await LoadPointsAsync(definition, from, to, cancellationToken)
                : await LoadBucketsAsync(definition, from, to, granularity, cancellationToken);

            lines.Add(new TrendSeriesDto
            {
                SeriesKey = definition.Key,
                Title = definition.Title,
                Unit = definition.Unit,
                DecimalPlaces = definition.DecimalPlaces,
                Granularity = granularity,
                Points = points
            });
        }

        return lines;
    }

    /// <summary>One point per stored row, for ranges short enough not to need bucketing.</summary>
    private async Task<IReadOnlyList<TrendPointDto>> LoadPointsAsync(
        SeriesDefinition definition,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken) =>
        await CurrentRows(definition, from, to)
            .OrderBy(o => o.ReferenceDate)
            .Select(o => new TrendPointDto
            {
                BucketStart = o.ReferenceDate,
                BucketEnd = o.ReferenceDate,
                Value = o.Value,
                Minimum = o.Value,
                Maximum = o.Value,
                ObservationCount = 1
            })
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Aggregated buckets, computed by SQL Server.
    /// </summary>
    /// <remarks>
    /// Grouped on <c>DATEPART</c> integers rather than a constructed date, because building a
    /// <see cref="DateOnly"/> inside a query is not translatable; the parts come back and become
    /// a date in <see cref="SeriesPeriods.BucketStartFromParts"/>.
    /// </remarks>
    private async Task<IReadOnlyList<TrendPointDto>> LoadBucketsAsync(
        SeriesDefinition definition,
        DateOnly from,
        DateOnly to,
        TrendGranularity granularity,
        CancellationToken cancellationToken)
    {
        var scope = CurrentRows(definition, from, to);

        var rows = granularity switch
        {
            TrendGranularity.Month => await scope
                .GroupBy(o => new { o.ReferenceDate.Year, Ordinal = o.ReferenceDate.Month })
                .Select(g => new BucketRow(
                    g.Key.Year, g.Key.Ordinal,
                    g.Average(o => o.Value), g.Min(o => o.Value), g.Max(o => o.Value), g.Count()))
                .ToListAsync(cancellationToken),

            TrendGranularity.Quarter => await scope
                .GroupBy(o => new
                {
                    o.ReferenceDate.Year,
                    Ordinal = ((o.ReferenceDate.Month - 1) / 3) + 1
                })
                .Select(g => new BucketRow(
                    g.Key.Year, g.Key.Ordinal,
                    g.Average(o => o.Value), g.Min(o => o.Value), g.Max(o => o.Value), g.Count()))
                .ToListAsync(cancellationToken),

            _ => await scope
                .GroupBy(o => o.ReferenceDate.Year)
                .Select(g => new BucketRow(
                    g.Key, 1,
                    g.Average(o => o.Value), g.Min(o => o.Value), g.Max(o => o.Value), g.Count()))
                .ToListAsync(cancellationToken)
        };

        return rows
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

    // --------------------------------------------------------------------- KPIs

    public async Task<IReadOnlyList<SeriesKpiDto>> GetKpisAsync(
        IReadOnlyList<string> seriesKeys,
        CancellationToken cancellationToken)
    {
        var requested = Resolve(seriesKeys);
        var kpis = new List<SeriesKpiDto>(requested.Count);

        foreach (var definition in requested)
        {
            var current = MeasureQueries.Rows(_db, definition).Where(o => o.IsCurrent);

            var recent = await current
                .OrderByDescending(o => o.ReferenceDate)
                .Take(2)
                .Select(o => new { o.ReferenceDate, o.Value, o.CollectedAtPkt })
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
                SeriesKey = definition.Key,
                Title = definition.Title,
                Unit = definition.Unit,
                DecimalPlaces = definition.DecimalPlaces,
                Frequency = definition.Frequency,
                SeasonalAdjustment = definition.SeasonalAdjustment,
                Latest = latest is null
                    ? null
                    : new SeriesLatestPointDto
                    {
                        ReferenceDate = latest.ReferenceDate,
                        Value = latest.Value,
                        CollectedAtPkt = latest.CollectedAtPkt
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

        // Monthly only for the CPI span: the annual and semiannual rows describe the same years
        // over again, so including them would not extend the history, only restate it.
        var cpiMonths = _db.CpiObservations.AsNoTracking()
            .Where(o => o.IsCurrent && o.PeriodType == PeriodType.Month);

        var sofrDays = _db.SofrDailyRates.AsNoTracking().Where(r => r.IsCurrent);

        return new DashboardSummaryDto
        {
            SourceCount = sourceCount,
            SeriesCount = SeriesCatalog.All.Count,
            CpiObservationCount = await _db.CpiObservations.AsNoTracking()
                .LongCountAsync(o => o.IsCurrent, cancellationToken),
            SofrObservationCount = await sofrDays.LongCountAsync(cancellationToken),

            // Nullable projections so an empty table returns null instead of throwing on Min/Max.
            EarliestCpiMonth = await cpiMonths
                .Select(o => (DateOnly?)o.ReferenceDate).MinAsync(cancellationToken),
            LatestCpiMonth = await cpiMonths
                .Select(o => (DateOnly?)o.ReferenceDate).MaxAsync(cancellationToken),
            EarliestSofrDate = await sofrDays
                .Select(r => (DateOnly?)r.EffectiveDate).MinAsync(cancellationToken),
            LatestSofrDate = await sofrDays
                .Select(r => (DateOnly?)r.EffectiveDate).MaxAsync(cancellationToken),

            LastCollectionAtPkt = await _db.CollectionRuns.AsNoTracking()
                .Where(r => r.Status == CollectionRunStatus.Succeeded
                    || r.Status == CollectionRunStatus.PartialSuccess)
                .Select(r => (DateTime?)r.StartedAtPkt)
                .MaxAsync(cancellationToken),

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
            .Where(r => r.StartedAtPkt >= since)
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
                .OrderByDescending(r => r.StartedAtPkt)
                .Select(r => new
                {
                    r.StartedAtPkt,
                    r.Status,
                    r.FailureCategory,
                    r.ErrorMessage
                })
                .FirstOrDefaultAsync(cancellationToken);

            var lastSuccess = await _db.CollectionRuns.AsNoTracking()
                .Where(r => r.DataSourceId == source.DataSourceId
                    && (r.Status == CollectionRunStatus.Succeeded
                        || r.Status == CollectionRunStatus.PartialSuccess))
                .OrderByDescending(r => r.StartedAtPkt)
                .Select(r => (DateTime?)r.StartedAtPkt)
                .FirstOrDefaultAsync(cancellationToken);

            var recentStatuses = await _db.CollectionRuns.AsNoTracking()
                .Where(r => r.DataSourceId == source.DataSourceId
                    && r.Status != CollectionRunStatus.Running)
                .OrderByDescending(r => r.StartedAtPkt)
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
                LastRunAtPkt = lastRun?.StartedAtPkt,
                LastSuccessAtPkt = lastSuccess,
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
            filtered = filtered.Where(r => r.StartedAtPkt >= fromUtc);
        }

        if (query.ToUtc is { } toUtc)
        {
            filtered = filtered.Where(r => r.StartedAtPkt <= toUtc);
        }

        var totalCount = await filtered.CountAsync(cancellationToken);

        if (totalCount == 0)
        {
            return PagedResult<CollectionRunDto>.Empty(query.Page);
        }

        var items = await RunProjection(filtered.OrderByDescending(r => r.StartedAtPkt))
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
            ScheduledForPkt = r.ScheduledForPkt,
            Attempt = r.Attempt,
            TriggerType = r.TriggerType,
            StartedAtPkt = r.StartedAtPkt,
            CompletedAtPkt = r.CompletedAtPkt,
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
    /// Current vintages for one series over a range — the shape every chart reads.
    /// </summary>
    private IQueryable<MeasureRow> CurrentRows(SeriesDefinition definition, DateOnly from, DateOnly to) =>
        MeasureQueries.Rows(_db, definition)
            .Where(o => o.IsCurrent && o.ReferenceDate >= from && o.ReferenceDate <= to);

    /// <summary>
    /// Resolves keys to catalogue entries, preserving the caller's order so the frontend can pair
    /// a colour with a series without re-sorting. Unknown keys are dropped rather than failing the
    /// request, so one stale bookmark cannot blank a whole dashboard.
    /// </summary>
    private static List<SeriesDefinition> Resolve(IReadOnlyList<string> seriesKeys)
    {
        var resolved = new List<SeriesDefinition>(seriesKeys.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in seriesKeys)
        {
            if (SeriesCatalog.TryGet(key, out var definition) && seen.Add(definition.Key))
            {
                resolved.Add(definition);
            }
        }

        return resolved;
    }

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

    /// <summary>One aggregated bucket as SQL Server returns it, before the date is rebuilt.</summary>
    private sealed record BucketRow(
        int Year,
        int Ordinal,
        decimal Average,
        decimal Minimum,
        decimal Maximum,
        int ObservationCount);
}
