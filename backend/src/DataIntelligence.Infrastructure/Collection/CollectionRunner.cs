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
/// Executes one collection cycle end to end and records exactly what happened.
/// </summary>
/// <remarks>
/// The contract that matters: this never throws for a collection failure. Every exit path
/// finalises the run record with a status and, on failure, a category — so the scheduler is
/// never taken down by a bad cycle (FR-2) and the reliability figures stay truthful.
/// </remarks>
public sealed class CollectionRunner : ICollectionRunner
{
    private readonly DataIntelligenceDbContext _db;
    private readonly ISourceFetcher _fetcher;
    private readonly ISourceParser _parser;
    private readonly IRobotsPolicy _robotsPolicy;
    private readonly CollectionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CollectionRunner> _logger;

    public CollectionRunner(
        DataIntelligenceDbContext db,
        ISourceFetcher fetcher,
        ISourceParser parser,
        IRobotsPolicy robotsPolicy,
        IOptions<CollectionOptions> options,
        TimeProvider timeProvider,
        ILogger<CollectionRunner> logger)
    {
        _db = db;
        _fetcher = fetcher;
        _parser = parser;
        _robotsPolicy = robotsPolicy;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<CollectionSummary> RunAsync(
        DateTime scheduledForUtc,
        CollectionTriggerType trigger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.SourceUrl))
        {
            _logger.LogWarning(
                "No source URL configured; skipping the cycle scheduled for {ScheduledFor:u}. "
                + "Set Collection:SourceUrl once the data source is signed off (SOW 0.1).",
                scheduledForUtc);

            return new CollectionSummary(null, CollectionRunStatus.Skipped, 0, 0, 0, 0, null,
                "No source URL configured.");
        }

        var run = await StartRunAsync(scheduledForUtc, trigger, cancellationToken);

        try
        {
            return await ExecuteAsync(run, cancellationToken);
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
            _logger.LogError(ex, "Collection run {RunId} failed: {Category}.", run.CollectionRunId, ex.Category);
            await FinaliseAsync(run, CollectionRunStatus.Failed, ex.Category, ex.Message, ex.ToString(), cancellationToken);
            return Summarise(run);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Collection run {RunId} could not be persisted.", run.CollectionRunId);
            await FinaliseAsync(run, CollectionRunStatus.Failed, CollectionFailureCategory.Persistence,
                ex.Message, ex.ToString(), cancellationToken);
            return Summarise(run);
        }
        catch (Exception ex)
        {
            // The backstop that keeps FR-2's promise: whatever went wrong, the scheduler lives.
            _logger.LogError(ex, "Collection run {RunId} failed unexpectedly.", run.CollectionRunId);
            await FinaliseAsync(run, CollectionRunStatus.Failed, CollectionFailureCategory.Unknown,
                ex.Message, ex.ToString(), cancellationToken);
            return Summarise(run);
        }
    }

    private async Task<CollectionSummary> ExecuteAsync(CollectionRun run, CancellationToken cancellationToken)
    {
        // 1. Compliance gate, before any request to the source itself (SOW 3).
        var robots = await _robotsPolicy.EvaluateAsync(_options.SourceUrl, cancellationToken);
        if (!robots.IsAllowed)
        {
            _logger.LogWarning("Collection disallowed by robots.txt: {Reason}", robots.Reason);

            // Skipped, not Failed: the platform behaved correctly. Counting this as a failure
            // would corrupt the reliability metric with a deliberate, correct decision.
            await FinaliseAsync(run, CollectionRunStatus.Skipped, null,
                $"Disallowed by robots.txt: {robots.Reason}", null, cancellationToken);
            return Summarise(run);
        }

        await RecordRobotsCheckAsync(cancellationToken);

        // 2. Fetch.
        var fetch = await _fetcher.FetchAsync(_options.SourceUrl, cancellationToken);
        run.HttpStatusCode = fetch.HttpStatusCode;

        if (!fetch.Succeeded)
        {
            await FinaliseAsync(run, CollectionRunStatus.Failed, fetch.FailureCategory,
                fetch.ErrorMessage, fetch.ErrorDetail, cancellationToken);
            return Summarise(run);
        }

        var content = fetch.Content!;

        if (_options.StoreRawPayload)
        {
            AddRawPayload(run, content, fetch.ContentType);
        }

        // 3. Parse. A ParseError propagates to the caller's catch and is categorised there.
        var parsed = _parser.Parse(content);
        run.RecordsFetched = parsed.Records.Count;

        if (parsed.RecordNodesMatched == 0)
        {
            // Fetched fine, parsed fine, matched nothing — the markup moved under us.
            await FinaliseAsync(run, CollectionRunStatus.Failed, CollectionFailureCategory.LayoutChanged,
                $"Record selector '{_options.Parser.RecordSelector}' matched no nodes in a "
                + $"{content.Length}-character response.",
                "The source's markup has probably changed. Re-parse the stored raw payload to confirm, "
                + "then update Collection:Parser.",
                cancellationToken);
            return Summarise(run);
        }

        // 4. Validate, then persist what survived.
        var collectedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var accepted = new List<ScrapedRecord>(parsed.Records.Count);

        foreach (var fragment in parsed.Rejections)
        {
            AddRejection(run, fragment.SourceKey, fragment.Reason, fragment.Detail, fragment.Fragment, collectedAtUtc);
        }

        foreach (var record in parsed.Records)
        {
            var failure = ScrapedRecordValidator.Validate(record, collectedAtUtc);
            if (failure is null)
            {
                accepted.Add(record);
                continue;
            }

            AddRejection(run, record.SourceKey, failure.Reason, failure.Detail, null, collectedAtUtc);
        }

        run.RecordsRejected = parsed.Rejections.Count + (parsed.Records.Count - accepted.Count);

        if (accepted.Count == 0)
        {
            await FinaliseAsync(run, CollectionRunStatus.Failed, CollectionFailureCategory.Validation,
                $"All {run.RecordsRejected} extracted records failed validation.",
                "See core.RejectedRecord for the per-record reasons.", cancellationToken);
            return Summarise(run);
        }

        await PersistAsync(run, accepted, collectedAtUtc, cancellationToken);

        // Partial success is a distinct state on purpose: data landed, but something was lost,
        // and that is worth surfacing on the dashboard rather than reporting a clean run.
        var status = run.RecordsRejected > 0
            ? CollectionRunStatus.PartialSuccess
            : CollectionRunStatus.Succeeded;

        await FinaliseAsync(run, status, null, null, null, cancellationToken);

        _logger.LogInformation(
            "Run {RunId} {Status}: {Fetched} fetched, {Inserted} inserted, {Unchanged} unchanged, {Rejected} rejected.",
            run.CollectionRunId, status, run.RecordsFetched, run.RecordsInserted,
            run.RecordsUnchanged, run.RecordsRejected);

        return Summarise(run);
    }

    /// <summary>
    /// Upserts items and writes snapshots, applying the deduplication rule (FR-3).
    /// </summary>
    private async Task PersistAsync(
        CollectionRun run,
        List<ScrapedRecord> records,
        DateTime collectedAtUtc,
        CancellationToken cancellationToken)
    {
        var categories = await ResolveCategoriesAsync(records, cancellationToken);
        var attributes = await ResolveAttributesAsync(records, cancellationToken);

        var sourceKeys = records.Select(r => r.SourceKey).ToList();

        var existingItems = await _db.Items
            .Where(i => sourceKeys.Contains(i.SourceKey))
            .ToDictionaryAsync(i => i.SourceKey, StringComparer.Ordinal, cancellationToken);

        // One query for every item's most recent hash, rather than one query per item. At a few
        // thousand items an hour the difference between this and an N+1 is the whole cycle.
        var itemIds = existingItems.Values.Select(i => i.ItemId).ToList();
        var latestHashes = await _db.ItemSnapshots
            .Where(s => itemIds.Contains(s.ItemId))
            .GroupBy(s => s.ItemId)
            .Select(g => new
            {
                ItemId = g.Key,
                RowHash = g.OrderByDescending(s => s.CollectedAtUtc)
                    .Select(s => s.RowHash)
                    .First()
            })
            .ToDictionaryAsync(x => x.ItemId, x => x.RowHash, cancellationToken);

        foreach (var record in records)
        {
            var rowHash = record.ComputeRowHash();

            if (!existingItems.TryGetValue(record.SourceKey, out var item))
            {
                item = new Item
                {
                    SourceKey = record.SourceKey,
                    Title = record.Title,
                    SourceUrl = record.SourceUrl,
                    FirstSeenRunId = run.CollectionRunId,
                    FirstSeenAtUtc = collectedAtUtc,
                    LastSeenAtUtc = collectedAtUtc,
                    IsActive = true
                };

                if (record.CategoryCode is { Length: > 0 } code && categories.TryGetValue(code, out var category))
                {
                    item.Category = category;
                }

                _db.Items.Add(item);
                existingItems[record.SourceKey] = item;
            }
            else
            {
                // Descriptive fields track the source; the observation history does not change.
                item.Title = record.Title;
                item.SourceUrl = record.SourceUrl;
                item.LastSeenAtUtc = collectedAtUtc;
                item.IsActive = true;

                if (record.CategoryCode is { Length: > 0 } code && categories.TryGetValue(code, out var category))
                {
                    item.Category = category;
                }
            }

            var unchanged = latestHashes.TryGetValue(item.ItemId, out var previousHash)
                && previousHash.AsSpan().SequenceEqual(rowHash);

            if (unchanged && !_options.StoreUnchangedSnapshots)
            {
                // FR-3. The observation is not lost: LastSeenAtUtc above records that the item
                // was present this cycle, and its values are the previous snapshot's.
                run.RecordsUnchanged++;
                continue;
            }

            var snapshot = new ItemSnapshot
            {
                Item = item,
                CollectionRunId = run.CollectionRunId,
                CollectedAtUtc = collectedAtUtc,
                PrimaryValue = record.PrimaryValue,
                SecondaryValue = record.SecondaryValue,
                Quantity = record.Quantity,
                StatusText = record.StatusText,
                CurrencyCode = record.CurrencyCode,
                PublishedAtUtc = record.PublishedAtUtc,
                RowHash = rowHash,
                HasChanged = !unchanged
            };

            foreach (var (code, value) in record.ExtraAttributes)
            {
                if (!attributes.TryGetValue(code, out var definition))
                {
                    continue;
                }

                snapshot.Attributes.Add(new SnapshotAttribute
                {
                    Attribute = definition,
                    CollectedAtUtc = collectedAtUtc,
                    ValueText = value
                });
            }

            _db.ItemSnapshots.Add(snapshot);

            if (unchanged)
            {
                run.RecordsUnchanged++;
            }
            else
            {
                run.RecordsInserted++;
            }
        }

        await MarkMissingItemsInactiveAsync(sourceKeys, collectedAtUtc, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Retires items that have not been seen for <c>InactiveAfterMissedCycles</c> cycles.
    /// </summary>
    /// <remarks>
    /// Deliberately lagged rather than "not in this payload". A single truncated response would
    /// otherwise retire the entire catalogue in one cycle, and the dashboards would show a cliff
    /// that never happened.
    /// </remarks>
    private async Task MarkMissingItemsInactiveAsync(
        List<string> seenKeys,
        DateTime collectedAtUtc,
        CancellationToken cancellationToken)
    {
        var cutoff = collectedAtUtc.AddMinutes(-_options.IntervalMinutes * _options.InactiveAfterMissedCycles);

        var stale = await _db.Items
            .Where(i => i.IsActive && i.LastSeenAtUtc < cutoff && !seenKeys.Contains(i.SourceKey))
            .ToListAsync(cancellationToken);

        foreach (var item in stale)
        {
            item.IsActive = false;
        }

        if (stale.Count > 0)
        {
            _logger.LogInformation(
                "Marked {Count} items inactive; not seen since before {Cutoff:u}.", stale.Count, cutoff);
        }
    }

    private async Task<Dictionary<string, Category>> ResolveCategoriesAsync(
        List<ScrapedRecord> records,
        CancellationToken cancellationToken)
    {
        var codes = records
            .Select(r => r.CategoryCode)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (codes.Count == 0)
        {
            return [];
        }

        var existing = await _db.Categories
            .Where(c => codes.Contains(c.Code))
            .ToDictionaryAsync(c => c.Code, StringComparer.Ordinal, cancellationToken);

        foreach (var code in codes.Where(c => !existing.ContainsKey(c)))
        {
            var displayName = records
                .FirstOrDefault(r => string.Equals(r.CategoryCode?.Trim(), code, StringComparison.Ordinal))
                ?.CategoryName;

            var category = new Category
            {
                Code = code,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? code : displayName
            };

            _db.Categories.Add(category);
            existing[code] = category;
        }

        return existing;
    }

    /// <summary>
    /// Registers attribute definitions on first sight, so a new source field is absorbed
    /// without a migration.
    /// </summary>
    private async Task<Dictionary<string, AttributeDefinition>> ResolveAttributesAsync(
        List<ScrapedRecord> records,
        CancellationToken cancellationToken)
    {
        var codes = records
            .SelectMany(r => r.ExtraAttributes.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (codes.Count == 0)
        {
            return [];
        }

        var existing = await _db.AttributeDefinitions
            .Where(a => codes.Contains(a.Code))
            .ToDictionaryAsync(a => a.Code, StringComparer.Ordinal, cancellationToken);

        foreach (var code in codes.Where(c => !existing.ContainsKey(c)))
        {
            var definition = new AttributeDefinition
            {
                Code = code,
                DisplayName = code,
                DataType = AttributeDataType.Text
            };

            _db.AttributeDefinitions.Add(definition);
            existing[code] = definition;

            _logger.LogInformation("Registered new source attribute '{Code}'.", code);
        }

        return existing;
    }

    private async Task<CollectionRun> StartRunAsync(
        DateTime scheduledForUtc,
        CollectionTriggerType trigger,
        CancellationToken cancellationToken)
    {
        // Attempt numbering makes a retry distinguishable from a fresh cycle under
        // UQ_CollectionRun_Cycle, so a retry cannot collide with the run it is retrying.
        var priorAttempts = await _db.CollectionRuns
            .CountAsync(r => r.ScheduledForUtc == scheduledForUtc, cancellationToken);

        var run = new CollectionRun
        {
            ScheduledForUtc = scheduledForUtc,
            Attempt = (byte)Math.Min(priorAttempts + 1, byte.MaxValue),
            TriggerType = trigger,
            StartedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
            Status = CollectionRunStatus.Running,
            RequestUrl = _options.SourceUrl
        };

        _db.CollectionRuns.Add(run);

        // Saved immediately so the run is visible while it is in flight, and so a hard crash
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

    private async Task RecordRobotsCheckAsync(CancellationToken cancellationToken)
    {
        var config = await _db.SourceConfigs
            .FirstOrDefaultAsync(c => c.SourceConfigId == SourceConfig.SingletonId, cancellationToken);

        if (config is not null)
        {
            config.RobotsTxtCheckedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        }
    }

    private void AddRawPayload(CollectionRun run, string content, string? contentType)
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
            ContentHash = SHA256.HashData(bytes),
            SizeBytes = bytes.Length,
            CompressedContent = output.ToArray()
        });
    }

    private void AddRejection(
        CollectionRun run,
        string? sourceKey,
        RejectionReason reason,
        string detail,
        string? fragment,
        DateTime rejectedAtUtc)
    {
        _db.RejectedRecords.Add(new RejectedRecord
        {
            CollectionRunId = run.CollectionRunId,
            SourceKey = Truncate(sourceKey, 200),
            RejectedAtUtc = rejectedAtUtc,
            Reason = reason,
            ReasonDetail = Truncate(detail, 1000),
            RawFragment = fragment
        });
    }

    private static CollectionSummary Summarise(CollectionRun run) => new(
        run.CollectionRunId,
        run.Status,
        run.RecordsFetched,
        run.RecordsInserted,
        run.RecordsUnchanged,
        run.RecordsRejected,
        run.FailureCategory,
        run.ErrorMessage);

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}
