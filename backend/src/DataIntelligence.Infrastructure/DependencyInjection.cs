using DataIntelligence.Core.Interfaces;
using DataIntelligence.Infrastructure.Collection;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DataIntelligence.Infrastructure;

/// <summary>
/// Registration for the infrastructure layer, so the API and the Worker wire it up identically
/// from one place (SOW 4.2 — both are deployable units over the same infrastructure).
/// </summary>
public static class DependencyInjection
{
    public const string ConnectionStringName = "DataIntelligenceDb";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Fail at startup with a specific message rather than at the first query with a
            // generic one. Secrets are supplied outside source control (SOW 3 — Security).
            throw new InvalidOperationException(
                $"Connection string '{ConnectionStringName}' is not configured. Set it via user "
                + "secrets locally or an environment variable in deployed environments.");
        }

        services.AddDbContext<DataIntelligenceDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsHistoryTable("__EFMigrationsHistory", "dbo");

                // Transient SQL faults (failover, throttling) are retried inside the provider, so
                // a momentary blip does not cost a whole collection cycle.
                sql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null);
            }));

        return services;
    }

    /// <summary>
    /// Registers the collection pipeline. Separate from <see cref="AddInfrastructure"/> because
    /// the API hosts the read side and does not need a fetcher or a parser.
    /// </summary>
    public static IServiceCollection AddCollection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<CollectionOptions>()
            .Bind(configuration.GetSection(CollectionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddMemoryCache();
        services.TryAddSingletonTimeProvider();

        services.AddHttpClient<ISourceFetcher, HttpSourceFetcher>(HttpSourceFetcher.HttpClientName, ConfigureClient);
        services.AddHttpClient<IRobotsPolicy, RobotsTxtPolicy>(ConfigureClient);

        services.AddScoped<ISourceParser, SelectorHtmlParser>();
        services.AddScoped<ICollectionRunner, CollectionRunner>();

        return services;

        static void ConfigureClient(IServiceProvider provider, HttpClient client)
        {
            var options = provider.GetRequiredService<IOptions<CollectionOptions>>().Value;

            // Identify the collector to the source operator, per common crawling etiquette. This
            // is also the token matched against robots.txt user-agent groups.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/json");

            // Per-request timeouts are enforced with a CancellationTokenSource so a timeout is
            // distinguishable from shutdown; the client-level timeout is the outer backstop.
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds * 2);
        }
    }

    private static IServiceCollection TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        // Injected rather than using DateTime.UtcNow directly so schedule alignment and the
        // validator's future-timestamp rule are deterministic under test.
        if (services.All(d => d.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }

        return services;
    }
}
