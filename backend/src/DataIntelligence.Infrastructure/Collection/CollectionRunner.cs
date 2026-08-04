using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DataIntelligence.Core.Collection;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Exceptions;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataIntelligence.Infrastructure.Collection;

/// <summary>
/// Executes one collection cycle for one source and records exactly what happened.
/// </summary>
/// <remarks>
/// The contract that matters: this never throws for a collection failure. Every exit path
/// finalises the run with a status and, on failure, a category — so a bad cycle from one
/// publisher can neither stop the scheduler (FR-2) nor affect the other publisher's run.
/// </remarks>
public sealed class CollectionRunner : ICollectionRunner
{
    private readonly DataIntelligenceDbContext _db;
    private readonly ISourceFetcher _fetcher;
    private readonly IEnumerable<ISourceAdapter> _adapters;
    private readonly IRobotsPolicy _robotsPolicy;
    private readonly CollectionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CollectionRunner> _logger;

    public CollectionRunner(
        DataIntelligenceDbContext db,
        ISourceFetcher fetcher,
        IEnumerable<ISourceAdapter> adapters,
        IRobotsPolicy robotsPolicy,
        IOptions<CollectionOptions> options,
        TimeProvider timeProvider,
        ILogger<CollectionRunner> logger)
    {
        _db = db;
        _fetcher = fetcher;
        _adapters = adapters;
        _robotsPolicy = robotsPolicy;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<CollectionSummary> RunAsync(
        string sourceCode,
        DateTime scheduledForUtc,
        CollectionTriggerType trigger,
        CancellationToken cancellationToken)
    {
        var source = await _db.DataSources
            .FirstOrDefaultAsync(s => s.Code == sourceCode, cancellationToken);

        if (source is null)
        {
            _logger.LogError("No data source is registered with code '{SourceCode}'.", sourceCode);
            return Skipped(sourceCode, $"No data source registered with code '{sourceCode}'.");
        }

        if (!source.IsEnabled)
        {
            _logger.LogInformation("Source {SourceCode} is disabled; skipping.", sourceCode);
            return Skipped(sourceCode, "Source is disabled.");
        }

        var adapter = _adapters.FirstOrDefault(a =>
            string.Equals(a.SourceCode, sourceCode, StringComparison.Ordinal));

        if (adapter is null)
        {
            _logger.LogError("No adapter is registered for source {SourceCode}.", sourceCode);
            return Skipped(sourceCode, $"No adapter registered for '{sourceCode}'.");
        }

        var run = await StartRunAsync(source, scheduledForUtc, trigger, cancellationToken);

        try
        {
            return await ExecuteAsync(source, adapter, run, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown mid-cycle. Leave a truthful record rather than a run stuck in 'Running'.
            await FinaliseAsync(run, CollectionRunStatus.Failed, CollectionFailureCategory.Unknown,
                "The service shut down while the cycle was in progress.", null, CancellationToken.None);
            throw;
        }
        catch (CollectionFailureException ex)
        {
            _logger.LogError(ex, "{Source}: run {RunId} failed ({Category}).",
                source.Code, run.CollectionRunId, ex.Category);
            await FinaliseAsync(run, CollectionRunStatus.Failed, ex.Category, ex.Message, ex.ToString(), cancellationToken);
            return Summarise(source.Code, run);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "{Source}: run {RunId} could not be persisted.", source.Code, run.CollectionRunId);
            await FinaliseAsync(run, CollectionRunStatus.Failed, CollectionFailureCategory.Persistence,
                ex.Message, ex.ToString(), cancellationToken);
            return Summarise(source.Code, run);
        }
        catch (Exception ex)
        {
            // The backstop that keeps FR-2's promise: whatever went wrong, the scheduler lives.
            _logger.LogError(ex, "{Source}: run {RunId} failed unexpectedly.", source.Code, run.CollectionRunId);
            await FinaliseAsync(run, CollectionRunStatus.Failed, CollectionFailureCategory.Unknown,
                ex.Message, ex.ToString(), cancellationToken);
            return Summarise(source.Code, run);
        }
    }

    private async Task<CollectionSummary> ExecuteAsync(
        DataSource source, ISourceAdapter adapter, CollectionRun run, CancellationToken cancellationToken)
    {
        // 1. Compliance gate. Scoped to HTML sources: RFC 9309 governs crawlers of web content,
        //    while both confirmed sources are official APIs published for programmatic use and
        //    carry their own terms (DataSource.TermsOfUseUrl).
        if (source.AccessMethod == SourceAccessMethod.Html && _options.RespectRobotsTxtForHtmlSources)
        {
            var robots = await _robotsPolicy.EvaluateAsync(source.ApiEndpoint, cancellationToken);
            if (!robots.IsAllowed)
            {
                _logger.LogWarning("{Source}: disallowed by robots.txt: {Reason}", source.Code, robots.Reason);

                // Skipped, not Failed: the platform behaved correctly, and counting a deliberate
                // decision as a failure would corrupt the reliability metric.
                await FinaliseAsync(run, CollectionRunStatus.Skipped, null,
                    $"Disallowed by robots.txt: {robots.Reason}", null, cancellationToken);
                return Summarise(source.Code, run);
            }

            source.RobotsTxtCheckedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        }

        // 2. Build and send the request.
        var seriesCodes = await _db.Series
            .Where(s => s.DataSourceId == source.DataSourceId && s.IsActive)
            .Select(s => s.SeriesCode)
            .ToListAsync(cancellationToken);

        var request = adapter.BuildRequest(
            new SourceRequestContext(source, seriesCodes, _timeProvider.GetUtcNow().UtcDateTime));

        run.RequestUrl = request.Url;

        var fetch = await _fetcher.FetchAsync(request, source, cancellationToken);
        run.HttpStatusCode = fetch.HttpStatusCode;

        if (!fetch.Succeeded)
        {
            await FinaliseAsync(run, CollectionRunStatus.Failed, fetch.FailureCategory,
                fetch.ErrorMessage, fetch.ErrorDetail, cancellationToken);
            return Summarise(source.Code, run);
        }

        var content = fetch.Content!;
        var contentHash = SHA256.HashData(Encoding.UTF8.GetBytes(content));

        if (_options.StoreRawPayload)
        {
            AddRawPayload(run, content, contentHash, fetch.ContentType);
        }

        // 3. Short-circuit an identical body. Polling monthly CPI hourly means the overwhelming
        //    majority of cycles return byte-for-byte what we already have; parsing and hashing
        //    every observation to discover that is wasted work.
        if (await IsUnchangedSinceLastRunAsync(source, run, contentHash, cancellationToken))
        {
            _logger.LogInformation(
                "{Source}: response is byte-identical to the previous run; nothing to parse.", source.Code);
            await FinaliseAsync(run, CollectionRunStatus.Succeeded, null, null, null, cancellationToken);
            return Summarise(source.Code, run);
        }

        // 4. Parse. A ParseError or SchemaChanged propagates to the caller's catch.
        var parsed = adapter.Parse(content);
        run.ObservationsFetched = parsed.Records.Count;

        if (parsed.EntriesSeen == 0)
        {
            await FinaliseAsync(run, CollectionRunStatus.Failed, CollectionFailureCategory.SchemaChanged,
                "The response parsed but contained no data entries.",
                "Re-parse the stored raw payload to confirm, then check the publisher's API contract.",
                cancellationToken);
            return Summarise(source.Code, run);
        }

        // 5. Validate.
        var collectedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var accepted = new List<ObservationRecord>(parsed.Records.Count);

        foreach (var fragment in parsed.Rejections)
        {
            AddRejection(run, fragment.SeriesCode, fragment.ReferenceDateText,
                fragment.Reason, fragment.Detail, fragment.Fragment, collectedAtUtc);
        }

        foreach (var record in parsed.Records)
        {
            var failure = ObservationValidator.Validate(record, collectedAtUtc);
            if (failure is null)
            {
                accepted.Add(record);
                continue;
            }

            AddRejection(run, record.SeriesCode, record.ReferenceDate.ToString("O"),
                failure.Reason, failure.Detail, null, collectedAtUtc);
        }

        run.ObservationsRejected = parsed.Rejections.Count + (parsed.Records.Count - accepted.Count);

        if (accepted.Count == 0)
        {
            await FinaliseAsync(run, CollectionRunStatus.Failed, CollectionFailureCategory.Validation,
                $"All {run.ObservationsRejected} extracted observations failed validation.",
                "See core.RejectedObservation for the per-record reasons.", cancellationToken);
            return Summarise(source.Code, run);
        }

        await PersistAsync(source, run, accepted, collectedAtUtc, cancellationToken);

        // Partial success is distinct on purpose: data landed, but something was lost, and that
        // is worth surfacing rather than reporting a clean run.
        var status = run.ObservationsRejected > 0
            ? CollectionRunStatus.PartialSuccess
            : CollectionRunStatus.Succeeded;

        await FinaliseAsync(run, status, null, null, null, cancellationToken);

        _logger.LogInformation(
            "{Source}: run {RunId} {Status}. {Fetched} fetched, {Inserted} new, {Revised} revised, "
            + "{Unchanged} unchanged, {Rejected} rejected.",
            source.Code, run.CollectionRunId, status, run.ObservationsFetched,
            run.ObservationsInserted, run.ObservationsRevised, run.ObservationsUnchanged,
            run.ObservationsRejected);

        return Summarise(source.Code, run);
    }

    /// <summary>
    /// Writes observations, applying deduplication (FR-3) and the revision rule (FR-4).
    /// </summary>
    /// <remarks>
    /// Three outcomes per record: unseen period becomes revision 0; unchanged value writes
    /// nothing; changed value supersedes the current vintage and appends the next one. Nothing
    /// is ever updated in place except the IsCurrent flag being cleared.
    /// </remarks>
    private async Task PersistAsync(
        DataSource source,
        CollectionRun run,
        List<ObservationRecord> records,
        DateTime collectedAtUtc,
        CancellationToken cancellationToken)
    {
        var seriesByCode = await _db.Series
            .Where(s => s.DataSourceId == source.DataSourceId)
            .ToDictionaryAsync(s => s.SeriesCode, StringComparer.Ordinal, cancellationToken);

        var seriesIds = seriesByCode.Values.Select(s => s.SeriesId).ToList();
        var referenceDates = records.Select(r => r.ReferenceDate).Distinct().ToList();

        // One query for every current vintage in range, rather than one per record. With ten
        // years of CPI history the difference between this and an N+1 is the whole cycle.
        var currentVintages = await _db.Observations
            .Where(o => seriesIds.Contains(o.SeriesId)
                     && o.IsCurrent
                     && referenceDates.Contains(o.ReferenceDate))
            .ToDictionaryAsync(o => (o.SeriesId, o.ReferenceDate), cancellationToken);

        foreach (var record in records)
        {
            if (!seriesByCode.TryGetValue(record.SeriesCode, out var series))
            {
                // Series are curated and seeded, so an unknown code means the publisher returned
                // something we did not ask for. Logged rather than auto-created: silently
                // inventing series is how a typo becomes permanent reference data.
                AddRejection(run, record.SeriesCode, record.ReferenceDate.ToString("O"),
                    RejectionReason.UnknownSeries,
                    $"Series '{record.SeriesCode}' is not registered for source {source.Code}.",
                    null, collectedAtUtc);
                run.ObservationsRejected++;
                continue;
            }

            series.LastSeenAtUtc = collectedAtUtc;
            series.FirstSeenAtUtc ??= collectedAtUtc;
            series.FirstSeenRunId ??= run.CollectionRunId;

            var rowHash = record.ComputeRowHash();

            if (!currentVintages.TryGetValue((series.SeriesId, record.ReferenceDate), out var current))
            {
                _db.Observations.Add(NewObservation(record, series.SeriesId, run, collectedAtUtc, rowHash, 0));
                run.ObservationsInserted++;
                continue;
            }

            // The publisher reissued the same figure. Nothing to record: the run itself is the
            // evidence that we checked, and a second identical vintage is not merely wasteful —
            // UQ_Observation_Current forbids it outright (FR-3).
            if (current.RowHash.AsSpan().SequenceEqual(rowHash))
            {
                run.ObservationsUnchanged++;
                continue;
            }

            // A genuine revision, in the order the unique index requires: release the current
            // flag before claiming it, or the insert collides with the row it replaces.
            current.IsCurrent = false;
            current.SupersededAtUtc = collectedAtUtc;

            _db.Observations.Add(NewObservation(
                record, series.SeriesId, run, collectedAtUtc, rowHash, (short)(current.RevisionNumber + 1)));

            run.ObservationsRevised++;

            _logger.LogInformation(
                "{Source}: {SeriesCode} {ReferenceDate:yyyy-MM-dd} revised from {Old} to {New}.",
                source.Code, record.SeriesCode, record.ReferenceDate, current.Value, record.Value);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static Observation NewObservation(
        ObservationRecord record, int seriesId, CollectionRun run,
        DateTime collectedAtUtc, byte[] rowHash, short revisionNumber) =>
        new()
        {
            SeriesId = seriesId,
            ReferenceDate = record.ReferenceDate,
            PeriodType = record.PeriodType,
            SourcePeriodCode = record.SourcePeriodCode,
            RevisionNumber = revisionNumber,
            IsCurrent = true,
            Value = record.Value,
            SourceAnnotation = record.SourceAnnotation,
            CollectionRunId = run.CollectionRunId,
            CollectedAtUtc = collectedAtUtc,
            RowHash = rowHash
        };

    /// <summary>
    /// Whether this response is byte-identical to the last successfully fetched one.
    /// </summary>
    private async Task<bool> IsUnchangedSinceLastRunAsync(
        DataSource source, CollectionRun run, byte[] contentHash, CancellationToken cancellationToken)
    {
        if (!_options.StoreRawPayload)
        {
            // Without stored payloads there is nothing to compare against, so the parse proceeds
            // and the per-observation hash catches the duplication instead.
            return false;
        }

        var previousHash = await _db.RawPayloads
            .Where(p => p.Run!.DataSourceId == source.DataSourceId
                     && p.CollectionRunId != run.CollectionRunId)
            .OrderByDescending(p => p.FetchedAtUtc)
            .Select(p => p.ContentHash)
            .FirstOrDefaultAsync(cancellationToken);

        return previousHash is not null && previousHash.AsSpan().SequenceEqual(contentHash);
    }

    private async Task<CollectionRun> StartRunAsync(
        DataSource source, DateTime scheduledForUtc, CollectionTriggerType trigger,
        CancellationToken cancellationToken)
    {
        // Attempt numbering keeps a retry distinguishable from the run it retries under
        // UQ_CollectionRun_Cycle, which is scoped per source.
        var priorAttempts = await _db.CollectionRuns
            .CountAsync(r => r.DataSourceId == source.DataSourceId
                          && r.ScheduledForUtc == scheduledForUtc, cancellationToken);

        var run = new CollectionRun
        {
            DataSourceId = source.DataSourceId,
            ScheduledForUtc = scheduledForUtc,
            Attempt = (byte)Math.Min(priorAttempts + 1, byte.MaxValue),
            TriggerType = trigger,
            StartedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
            Status = CollectionRunStatus.Running,
            RequestUrl = source.ApiEndpoint
        };

        _db.CollectionRuns.Add(run);

        // Saved immediately so the run is visible while in flight, and so a hard crash still
        // leaves evidence that the cycle started.
        await _db.SaveChangesAsync(cancellationToken);
        return run;
    }

    private async Task FinaliseAsync(
        CollectionRun run,
        CollectionRunStatus status,
        CollectionFailureCategory? failureCategory,
        string? errorMessage,
        string? errorDetail,
        CancellationToken cancellationToken)
    {
        run.Status = status;
        run.CompletedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        run.FailureCategory = failureCategory;
        run.ErrorMessage = Truncate(errorMessage, 1000);
        run.ErrorDetail = errorDetail;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Last resort. If even the failure record cannot be written the database is the
            // problem; log it and let the scheduler continue rather than crashing the service.
            _logger.LogCritical(ex, "Could not write the outcome of run {RunId}.", run.CollectionRunId);
        }
    }

    private void AddRawPayload(CollectionRun run, string content, byte[] contentHash, string? contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(content);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }

        _db.RawPayloads.Add(new RawPayload
        {
            CollectionRunId = run.CollectionRunId,
            FetchedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
            ContentType = Truncate(contentType, 100),
            ContentHash = contentHash,
            SizeBytes = bytes.Length,
            CompressedContent = output.ToArray()
        });
    }

    private void AddRejection(
        CollectionRun run, string? seriesCode, string? referenceDateText,
        RejectionReason reason, string detail, string? fragment, DateTime rejectedAtUtc)
    {
        _db.RejectedObservations.Add(new RejectedObservation
        {
            CollectionRunId = run.CollectionRunId,
            SeriesCode = Truncate(seriesCode, 100),
            ReferenceDateText = Truncate(referenceDateText, 50),
            RejectedAtUtc = rejectedAtUtc,
            Reason = reason,
            ReasonDetail = Truncate(detail, 1000),
            RawFragment = fragment
        });
    }

    private static CollectionSummary Skipped(string sourceCode, string reason) =>
        new(sourceCode, null, CollectionRunStatus.Skipped, 0, 0, 0, 0, 0, null, reason);

    private static CollectionSummary Summarise(string sourceCode, CollectionRun run) => new(
        sourceCode,
        run.CollectionRunId,
        run.Status,
        run.ObservationsFetched,
        run.ObservationsInserted,
        run.ObservationsRevised,
        run.ObservationsUnchanged,
        run.ObservationsRejected,
        run.FailureCategory,
        run.ErrorMessage);

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
