using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Infrastructure.Collection;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DataIntelligence.Worker;

/// <summary>
/// Runs the collection cycle on a schedule, independently of API traffic (FR-1, FR-8).
/// </summary>
/// <remarks>
/// Owns only <em>when</em> collection happens; <see cref="ICollectionRunner"/> owns what it does.
/// The loop's one job beyond timing is to never die: a cycle that throws is logged and the next
/// one still fires, which is what the ≥99% success rate over a rolling 30 days depends on.
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

        await SyncSourceConfigAsync(stoppingToken);

        if (string.IsNullOrWhiteSpace(_options.SourceUrl))
        {
            // The service stays up so it is deployable and health-checkable before the source is
            // signed off (SOW 0.1); it simply has nothing to collect yet.
            _logger.LogWarning(
                "No Collection:SourceUrl is configured. The worker is idle until the data source "
                + "is confirmed ([DATA SOURCE - TBD], SOW 0.1).");
            return;
        }

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

            await RunCycleSafelyAsync(nextRun, stoppingToken);
        }

        _logger.LogInformation("Collection worker stopped.");
    }

    /// <summary>
    /// Runs one cycle and swallows anything it throws. This is the outermost guarantee behind
    /// FR-2: no single cycle can stop the scheduler.
    /// </summary>
    private async Task RunCycleSafelyAsync(DateTime scheduledForUtc, CancellationToken stoppingToken)
    {
        try
        {
            // A scope per cycle: the DbContext and the runner are scoped, and a long-lived
            // context would accumulate tracked entities for the life of the service.
            using var scope = _scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<ICollectionRunner>();

            var summary = await runner.RunAsync(
                scheduledForUtc, CollectionTriggerType.Scheduled, stoppingToken);

            if (summary.Status is CollectionRunStatus.Failed)
            {
                _logger.LogError(
                    "Cycle {ScheduledFor:u} failed ({Category}): {Message}",
                    scheduledForUtc, summary.FailureCategory, summary.ErrorMessage);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown during a cycle. The runner has already recorded the outcome.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Cycle {ScheduledFor:u} threw outside the runner's own handling. "
                + "The schedule continues.", scheduledForUtc);
        }
    }

    /// <summary>
    /// Writes the configured source into <c>collect.SourceConfig</c>, so the database itself
    /// records what the platform is pointed at and data lineage is answerable in SQL.
    /// </summary>
    private async Task SyncSourceConfigAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DataIntelligenceDbContext>();

            var config = await db.SourceConfigs
                .FirstOrDefaultAsync(c => c.SourceConfigId == SourceConfig.SingletonId, cancellationToken);

            var baseUrl = Uri.TryCreate(_options.SourceUrl, UriKind.Absolute, out var uri)
                ? $"{uri.Scheme}://{uri.Authority}"
                : string.Empty;

            if (config is null)
            {
                db.SourceConfigs.Add(new SourceConfig
                {
                    SourceConfigId = SourceConfig.SingletonId,
                    Name = _options.SourceName,
                    BaseUrl = baseUrl,
                    CollectionUrl = _options.SourceUrl,
                    CollectionIntervalMinutes = (short)_options.IntervalMinutes,
                    RequestTimeoutSec = (short)_options.RequestTimeoutSeconds,
                    MaxRetries = (byte)_options.MaxRetries,
                    UserAgent = _options.UserAgent,
                    IsEnabled = !string.IsNullOrWhiteSpace(_options.SourceUrl),
                    CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime
                });
            }
            else
            {
                config.Name = _options.SourceName;
                config.BaseUrl = baseUrl;
                config.CollectionUrl = _options.SourceUrl;
                config.CollectionIntervalMinutes = (short)_options.IntervalMinutes;
                config.RequestTimeoutSec = (short)_options.RequestTimeoutSeconds;
                config.MaxRetries = (byte)_options.MaxRetries;
                config.UserAgent = _options.UserAgent;
                config.IsEnabled = !string.IsNullOrWhiteSpace(_options.SourceUrl);
                config.UpdatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Lineage bookkeeping, not a prerequisite for collecting. Log and carry on.
            _logger.LogWarning(ex, "Could not synchronise collect.SourceConfig from configuration.");
        }
    }
}
