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
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<Worker> _logger;

    public Worker(
        IServiceScopeFactory scopeFactory,
        IOptions<CollectionOptions> options,
        TimeProvider timeProvider,
        ILogger<Worker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

            await RunCycleAsync(nextRun, stoppingToken);
        }

        _logger.LogInformation("Collection worker stopped.");
    }

    /// <summary>
    /// Runs one cycle for every enabled source, in sequence.
    /// </summary>
    /// <remarks>
    /// Sequential rather than parallel: two sources at hourly cadence take seconds, and running
    /// them in order keeps the logs readable and avoids two writers contending over the same
    /// tables for no measurable gain.
    /// </remarks>
    private async Task RunCycleAsync(DateTime scheduledForUtc, CancellationToken stoppingToken)
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
            await RunSourceSafelyAsync(sourceCode, scheduledForUtc, stoppingToken);
        }
    }

    /// <summary>
    /// Runs one source and swallows anything it throws. The outermost guarantee behind FR-2:
    /// no single source can stop the scheduler or its sibling.
    /// </summary>
    private async Task RunSourceSafelyAsync(
        string sourceCode, DateTime scheduledForUtc, CancellationToken stoppingToken)
    {
        try
        {
            // A scope per source: the DbContext and runner are scoped, and a long-lived context
            // would accumulate tracked entities for the life of the service.
            using var scope = _scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<ICollectionRunner>();

            var summary = await runner.RunAsync(
                sourceCode, scheduledForUtc, CollectionTriggerType.Scheduled, stoppingToken);

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
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown during a cycle. The runner has already recorded the outcome.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "{Source}: cycle {ScheduledFor:u} threw outside the runner's own handling. "
                + "The schedule continues.", sourceCode, scheduledForUtc);
        }
    }
}
