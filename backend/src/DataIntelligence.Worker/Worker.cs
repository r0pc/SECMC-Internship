using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Infrastructure.Collection;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DataIntelligence.Worker;

/// <summary>
/// Runs the collection cycle for every enabled source on a schedule, independently of API
/// traffic (FR-1, FR-8).
/// </summary>
/// <remarks>
/// Owns only <em>when</em> collection happens; <see cref="ICollectionRunner"/> owns what it
/// does. The loop's job beyond timing is to never die: one source failing must not stop the
/// other, and neither may stop the schedule — that is what the >=99% success rate depends on.
/// </remarks>
public class Worker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CollectionOptions _options;
    private readonly WorkerRunMode _runMode;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<Worker> _logger;

    public Worker(
        IServiceScopeFactory scopeFactory,
        IOptions<CollectionOptions> options,
        WorkerRunMode runMode,
        IHostApplicationLifetime lifetime,
        TimeProvider timeProvider,
        ILogger<Worker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _runMode = runMode;
        _lifetime = lifetime;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_runMode.IsOneShot)
        {
            try
            {
                if (_runMode.Mode == WorkerMode.Backfill)
                {
                    await RunBackfillAsync(stoppingToken);
                }
                else
                {
                    await RunOnceAsync(stoppingToken);
                }
            }
            finally
            {
                // Stops the host rather than simply returning: returning from ExecuteAsync leaves
                // a BackgroundService's host running with nothing left to do.
                _lifetime.StopApplication();
            }

            return;
        }

        _logger.LogInformation(
            "Collection worker started. Interval {Interval} minutes, clock alignment {Alignment}.",
            _options.IntervalMinutes, _options.AlignToClock ? "on" : "off");

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var nextRun = CollectionSchedule.GetNextRunTime(
                now, TimeSpan.FromMinutes(_options.IntervalMinutes), _options.AlignToClock);

            _logger.LogInformation("Next collection cycle at {NextRun:u}.", nextRun);

            try
            {
                await Task.Delay(nextRun - now, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RunCycleAsync(nextRun, CollectionTriggerType.Scheduled, stoppingToken);
        }

        _logger.LogInformation("Collection worker stopped.");
    }

    /// <summary>
    /// Collects once, now, and shuts the host down.
    /// </summary>
    /// <remarks>
    /// Recorded as <see cref="CollectionTriggerType.Manual"/> rather than Scheduled, so the run
    /// log distinguishes a cycle someone asked for from one the timer produced — which matters
    /// when reading the collection history after a manual backfill.
    /// <para>
    /// The cycle is stamped with the current time rather than the next scheduled boundary. That
    /// is what keeps it out of the way of the scheduled run for the same hour: the two would
    /// otherwise share <c>ScheduledForUtc</c> and the manual one would be filed as a retry of a
    /// cycle that had not happened.
    /// </para>
    /// </remarks>
    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        _logger.LogInformation("Running a single collection cycle on demand, then exiting.");

        await RunCycleAsync(now, CollectionTriggerType.Manual, stoppingToken);
    }

    /// <summary>
    /// Loads history for whichever datasets were asked for, then reports the total.
    /// </summary>
    /// <remarks>
    /// The scheduled cycle deliberately asks for a narrow, recent window — two years of CPI, the
    /// current year of SOFR — because that is what the dashboards read and re-requesting decades
    /// of settled figures every hour would be absurd. Loading the rest is this separate,
    /// deliberate act.
    /// <para>
    /// Every request is its own run with its own row in the collection log, so a failure part way
    /// through says exactly what landed. Re-running is safe: figures already stored hash as
    /// unchanged and write nothing.
    /// </para>
    /// </remarks>
    private async Task RunBackfillAsync(CancellationToken stoppingToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var total = new BackfillTally();

        if (_runMode.IncludeCpi)
        {
            total += await BackfillCpiAsync(now, stoppingToken);
        }

        if (_runMode.IncludeSofr && !stoppingToken.IsCancellationRequested)
        {
            // Offset past the CPI cycle times so the runs order readably in the log. Not required
            // for correctness — UQ_CollectionRun_Cycle is scoped per source — but a backfill read
            // back later is easier to follow in the order it happened.
            total += await BackfillSofrAsync(now.AddMinutes(1), stoppingToken);
        }

        if (total.Failures > 0)
        {
            _logger.LogWarning(
                "Backfill finished with {Failures} failed request(s); {Inserted} figures loaded. "
                + "Re-run to retry — figures already stored are recognised as unchanged.",
                total.Failures, total.Inserted);
        }
        else
        {
            _logger.LogInformation(
                "Backfill complete: {Inserted} figures loaded, {Revised} revised.",
                total.Inserted, total.Revised);
        }
    }

    /// <summary>
    /// Loads CPI from the requested year to now, in chunks the API will accept.
    /// </summary>
    /// <remarks>
    /// Chunked because BLS caps the span of a single request — see
    /// <see cref="BlsOptions.MaxYearsPerRequest"/>. The series runs back to 1913, so a full load
    /// is roughly six requests.
    /// </remarks>
    private async Task<BackfillTally> BackfillCpiAsync(DateTime now, CancellationToken stoppingToken)
    {
        var fromYear = _runMode.CpiFromYear;
        var toYear = now.Year;
        var chunkYears = Math.Max(1, _options.Bls.MaxYearsPerRequest);

        var windows = new List<CollectionWindow>();

        for (var start = fromYear; start <= toYear; start += chunkYears)
        {
            windows.Add(CollectionWindow.ForYears(start, Math.Min(start + chunkYears - 1, toYear)));
        }

        _logger.LogInformation(
            "Backfilling {Source} from {FromYear} to {ToYear}: {Chunks} request(s) of up to "
            + "{ChunkYears} years.",
            DataSource.BlsCpiCode, fromYear, toYear, windows.Count, chunkYears);

        var tally = new BackfillTally();

        for (var i = 0; i < windows.Count; i++)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning("CPI backfill cancelled after {Done} of {Total} chunks.",
                    i, windows.Count);
                break;
            }

            var window = windows[i];

            // A distinct cycle time per chunk. Sharing one would file each chunk as a retry of
            // the last under UQ_CollectionRun_Cycle, which is not what happened.
            var summary = await RunSourceSafelyAsync(
                DataSource.BlsCpiCode, now.AddSeconds(i), CollectionTriggerType.Backfill,
                window, stoppingToken);

            if (summary is null || summary.Status == CollectionRunStatus.Failed)
            {
                tally = tally.WithFailure();
                _logger.LogError("CPI backfill {From}-{To} failed.", window.FromYear, window.ToYear);
                continue;
            }

            tally = tally.With(summary);

            _logger.LogInformation(
                "CPI {From}-{To}: {Inserted} new, {Revised} revised, {Unchanged} unchanged, "
                + "{Rejected} rejected.",
                window.FromYear, window.ToYear, summary.Inserted, summary.Revised,
                summary.Unchanged, summary.Rejected);
        }

        return tally;
    }

    /// <summary>
    /// Loads the whole SOFR history in one request.
    /// </summary>
    /// <remarks>
    /// No chunking: the NY Fed search endpoint takes an arbitrary date range and returns the full
    /// series — a little over 2,000 business days since April 2018 — in a single response well
    /// inside the payload limit. There is correspondingly nothing for <c>--from</c> to choose.
    /// </remarks>
    private async Task<BackfillTally> BackfillSofrAsync(DateTime now, CancellationToken stoppingToken)
    {
        var window = new CollectionWindow(
            WorkerRunMode.FirstSofrDate, DateOnly.FromDateTime(now));

        _logger.LogInformation("Backfilling {Source} over {Window}: 1 request.",
            DataSource.NyFedSofrCode, window);

        var summary = await RunSourceSafelyAsync(
            DataSource.NyFedSofrCode, now, CollectionTriggerType.Backfill, window, stoppingToken);

        if (summary is null || summary.Status == CollectionRunStatus.Failed)
        {
            _logger.LogError("SOFR backfill over {Window} failed.", window);
            return new BackfillTally().WithFailure();
        }

        _logger.LogInformation(
            "SOFR {Window}: {Inserted} new, {Revised} revised, {Unchanged} unchanged, "
            + "{Rejected} rejected.",
            window, summary.Inserted, summary.Revised, summary.Unchanged, summary.Rejected);

        return new BackfillTally().With(summary);
    }

    /// <summary>Running totals across a backfill's requests.</summary>
    private readonly record struct BackfillTally(int Inserted = 0, int Revised = 0, int Failures = 0)
    {
        public BackfillTally With(CollectionSummary summary) =>
            this with { Inserted = Inserted + summary.Inserted, Revised = Revised + summary.Revised };

        public BackfillTally WithFailure() => this with { Failures = Failures + 1 };

        public static BackfillTally operator +(BackfillTally a, BackfillTally b) =>
            new(a.Inserted + b.Inserted, a.Revised + b.Revised, a.Failures + b.Failures);
    }

    /// <summary>
    /// Runs one cycle for every enabled source, in sequence.
    /// </summary>
    /// <remarks>
    /// Sequential rather than parallel: two sources at hourly cadence take seconds, and running
    /// them in order keeps the logs readable and avoids two writers contending over the same
    /// tables for no measurable gain.
    /// </remarks>
    private async Task RunCycleAsync(
        DateTime scheduledForUtc, CollectionTriggerType trigger, CancellationToken stoppingToken)
    {
        List<string> sourceCodes;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataIntelligenceDbContext>();

            sourceCodes = await db.DataSources
                .Where(s => s.IsEnabled)
                .OrderBy(s => s.DataSourceId)
                .Select(s => s.Code)
                .ToListAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // Without the source list there is nothing to run, but the schedule must survive:
            // the database may simply be restarting.
            _logger.LogError(ex, "Could not read the enabled sources; skipping this cycle.");
            return;
        }

        if (sourceCodes.Count == 0)
        {
            _logger.LogWarning(
                "No enabled data sources. Seed collect.DataSource, or enable a row, to begin collecting.");
            return;
        }

        foreach (var sourceCode in sourceCodes)
        {
            await RunSourceSafelyAsync(sourceCode, scheduledForUtc, trigger, null, stoppingToken);
        }
    }

    /// <summary>
    /// Runs one source and swallows anything it throws. The outermost guarantee behind FR-2:
    /// no single source can stop the scheduler or its sibling.
    /// </summary>
    private async Task<CollectionSummary?> RunSourceSafelyAsync(
        string sourceCode,
        DateTime scheduledForUtc,
        CollectionTriggerType trigger,
        CollectionWindow? window,
        CancellationToken stoppingToken)
    {
        try
        {
            // A scope per source: the DbContext and runner are scoped, and a long-lived context
            // would accumulate tracked entities for the life of the service.
            using var scope = _scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<ICollectionRunner>();

            var summary = await runner.RunAsync(
                sourceCode, scheduledForUtc, trigger, window, stoppingToken);

            if (_runMode.Mode == WorkerMode.Once)
            {
                // The whole output of a one-shot run, on one line, because nobody watching a
                // manual collection wants to go to the database to find out what it did.
                _logger.LogInformation(
                    "{Source}: {Status}. {Fetched} fetched, {Inserted} new, {Revised} revised, "
                    + "{Unchanged} unchanged, {Rejected} rejected.",
                    sourceCode, summary.Status, summary.Fetched, summary.Inserted,
                    summary.Revised, summary.Unchanged, summary.Rejected);
            }

            if (summary.Status is CollectionRunStatus.Failed)
            {
                _logger.LogError(
                    "{Source}: cycle {ScheduledFor:u} failed ({Category}): {Message}",
                    sourceCode, scheduledForUtc, summary.FailureCategory, summary.ErrorMessage);
            }
            else if (summary.Revised > 0)
            {
                // A revision means a published figure moved after release — worth its own line
                // in the log rather than being buried in the run counters.
                _logger.LogInformation(
                    "{Source}: {Revised} observation(s) revised by the publisher.",
                    sourceCode, summary.Revised);
            }

            return summary;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown during a cycle. The runner has already recorded the outcome.
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "{Source}: cycle {ScheduledFor:u} threw outside the runner's own handling. "
                + "The schedule continues.", sourceCode, scheduledForUtc);

            return null;
        }
    }
}
