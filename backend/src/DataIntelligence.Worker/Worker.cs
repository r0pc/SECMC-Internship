namespace DataIntelligence.Worker;

/// <summary>
/// Scaffold for the hourly collection cycle (FR-1). The scheduling loop, collector
/// invocation, failure logging (FR-2), and deduplication (FR-3) land in Phase 4.
/// </summary>
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Collection worker started. No collection schedule is configured yet.");
        return Task.CompletedTask;
    }
}
