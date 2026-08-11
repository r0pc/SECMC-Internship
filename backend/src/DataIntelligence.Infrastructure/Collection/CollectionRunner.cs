using DataIntelligence.Core;
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
    private readonly IEnumerable<IDatasetWriter> _writers;
    private readonly IRobotsPolicy _robotsPolicy;
    private readonly CollectionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CollectionRunner> _logger;

    public CollectionRunner(
        DataIntelligenceDbContext db,
        ISourceFetcher fetcher,
        IEnumerable<ISourceAdapter> adapters,
        IEnumerable<IDatasetWriter> writers,
        IRobotsPolicy robotsPolicy,
        IOptions<CollectionOptions> options,
        TimeProvider timeProvider,
        ILogger<CollectionRunner> logger)
    {
        _db = db;
        _fetcher = fetcher;
        _adapters = adapters;
        _writers = writers;
        _robotsPolicy = robotsPolicy;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<CollectionSummary> RunAsync(
        string sourceCode,
        DateTime scheduledForPkt,
        CollectionTriggerType trigger,
        CollectionWindow? window,
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

        // Resolved before the run starts: a source with an adapter but no writer would fetch,
        // parse, and then have nowhere to put the result — better to skip than to record a run
        // that looks like it did something.
        var writer = _writers.FirstOrDefault(w =>
            string.Equals(w.SourceCode, sourceCode, StringComparison.Ordinal));

        if (writer is null)
        {
            _logger.LogError("No dataset writer is registered for source {SourceCode}.", sourceCode);
            return Skipped(sourceCode, $"No dataset writer registered for '{sourceCode}'.");
        }

        var run = await StartRunAsync(source, scheduledForPkt, trigger, cancellationToken);

        try
        {
            return await ExecuteAsync(source, adapter, writer, run, window, cancellationToken);
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
        DataSource source, ISourceAdapter adapter, IDatasetWriter writer, CollectionRun run,
        CollectionWindow? window, CancellationToken cancellationToken)
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

            source.RobotsTxtCheckedAtPkt = PakistanTime.Now(_timeProvider);
        }

        // 2. Build and send the request.
        var request = adapter.BuildRequest(
            new SourceRequestContext(source, PakistanTime.Now(_timeProvider), window));

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
        var collectedAtPkt = PakistanTime.Now(_timeProvider);
        var accepted = new List<ObservationRecord>(parsed.Records.Count);

        foreach (var fragment in parsed.Rejections)
        {
            AddRejection(run, fragment.SeriesCode, fragment.ReferenceDateText,
                fragment.Reason, fragment.Detail, fragment.Fragment, collectedAtPkt);
        }

        foreach (var record in parsed.Records)
        {
            var failure = ObservationValidator.Validate(record, collectedAtPkt);
            if (failure is null)
            {
                accepted.Add(record);
                continue;
            }

            AddRejection(run, record.SeriesCode, record.ReferenceLabel,
                failure.Reason, failure.Detail, null, collectedAtPkt);
        }

        run.ObservationsRejected = parsed.Rejections.Count + (parsed.Records.Count - accepted.Count);

        if (accepted.Count == 0)
        {
            await FinaliseAsync(run, CollectionRunStatus.Failed, CollectionFailureCategory.Validation,
                $"All {run.ObservationsRejected} extracted observations failed validation.",
                "See core.RejectedObservation for the per-record reasons.", cancellationToken);
            return Summarise(source.Code, run);
        }

        // 6. Persist, in whichever table this dataset owns.
        var written = await writer.WriteAsync(run, accepted, collectedAtPkt, cancellationToken);

        run.ObservationsInserted = written.Inserted;
        run.ObservationsRevised = written.Revised;
        run.ObservationsUnchanged = written.Unchanged;

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
            .OrderByDescending(p => p.FetchedAtPkt)
            .Select(p => p.ContentHash)
            .FirstOrDefaultAsync(cancellationToken);

        return previousHash is not null && previousHash.AsSpan().SequenceEqual(contentHash);
    }

    private async Task<CollectionRun> StartRunAsync(
        DataSource source, DateTime scheduledForPkt, CollectionTriggerType trigger,
        CancellationToken cancellationToken)
    {
        // Attempt numbering keeps a retry distinguishable from the run it retries under
        // UQ_CollectionRun_Cycle, which is scoped per source.
        var priorAttempts = await _db.CollectionRuns
            .CountAsync(r => r.DataSourceId == source.DataSourceId
                          && r.ScheduledForPkt == scheduledForPkt, cancellationToken);

        var run = new CollectionRun
        {
            DataSourceId = source.DataSourceId,
            ScheduledForPkt = scheduledForPkt,
            Attempt = (byte)Math.Min(priorAttempts + 1, byte.MaxValue),
            TriggerType = trigger,
            StartedAtPkt = PakistanTime.Now(_timeProvider),
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
        run.CompletedAtPkt = PakistanTime.Now(_timeProvider);
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
            FetchedAtPkt = PakistanTime.Now(_timeProvider),
            ContentType = Truncate(contentType, 100),
            ContentHash = contentHash,
            SizeBytes = bytes.Length,
            CompressedContent = output.ToArray()
        });
    }

    private void AddRejection(
        CollectionRun run, string? seriesCode, string? referenceDateText,
        RejectionReason reason, string detail, string? fragment, DateTime rejectedAtPkt)
    {
        _db.RejectedObservations.Add(new RejectedObservation
        {
            CollectionRunId = run.CollectionRunId,
            SeriesCode = Truncate(seriesCode, 100),
            ReferenceDateText = Truncate(referenceDateText, 50),
            RejectedAtPkt = rejectedAtPkt,
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
