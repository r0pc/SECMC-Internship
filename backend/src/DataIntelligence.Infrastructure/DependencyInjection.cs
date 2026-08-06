using DataIntelligence.Core.Interfaces;
using DataIntelligence.Infrastructure.Ai;
using DataIntelligence.Infrastructure.Analytics;
using DataIntelligence.Infrastructure.Collection;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// Registers the read side the dashboards query (FR-7, FR-10). Separate from
    /// <see cref="AddCollection"/> because the Worker writes data and never serves it.
    /// </summary>
    public static IServiceCollection AddAnalytics(this IServiceCollection services)
    {
        // Both services take a TimeProvider so "last 30 days" and "12 months back from today"
        // are fixable in a test rather than depending on the wall clock.
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<ICatalogService, CatalogService>();
        services.AddScoped<IDashboardQueryService, DashboardQueryService>();

        return services;
    }

    /// <summary>
    /// Registers the collection pipeline. Separate from <see cref="AddInfrastructure"/> because
    /// the API hosts the read side and needs no fetcher or adapters.
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

        // Injected rather than using DateTime.UtcNow directly, so schedule alignment and the
        // validator's future-period rule stay deterministic under test.
        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient<ISourceFetcher, HttpSourceFetcher>(
            HttpSourceFetcher.HttpClientName, ConfigureClient);
        services.AddHttpClient<IRobotsPolicy, RobotsTxtPolicy>(ConfigureClient);

        // One adapter and one writer per dataset, both resolved by SourceCode: the adapter knows
        // the publisher's payload, the writer knows the table. Adding a third dataset is a pair
        // of registrations here, a table, and nothing else.
        services.AddScoped<ISourceAdapter, BlsCpiAdapter>();
        services.AddScoped<ISourceAdapter, SofrAdapter>();

        services.AddScoped<IDatasetWriter, CpiObservationWriter>();
        services.AddScoped<IDatasetWriter, SofrDailyRateWriter>();

        services.AddScoped<ICollectionRunner, CollectionRunner>();

        return services;

        static void ConfigureClient(IServiceProvider provider, HttpClient client)
        {
            var options = provider.GetRequiredService<IOptions<CollectionOptions>>().Value;

            // Identify the collector to the publisher, per common etiquette for automated
            // clients, and so a maintainer can be contacted rather than simply blocked.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

            // Per-request timeouts are enforced with a CancellationTokenSource so a timeout is
            // distinguishable from shutdown; the client-level timeout is the outer backstop.
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds * 2);
        }
    }
    /// <summary>Registers the AI query assistant (FR-13 – FR-16).</summary>
    /// <remarks>
    /// Deliberately does not <c>ValidateOnStart</c>: the assistant's API key is not required for
    /// the dashboards to work, and a missing key must not take the whole API down with it. The
    /// key is checked when a question is actually asked, which turns a missing secret into one
    /// failed endpoint rather than a process that will not boot.
    /// </remarks>
    public static IServiceCollection AddAssistant(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AssistantOptions>()
            .Bind(configuration.GetSection(AssistantOptions.SectionName))
            .ValidateDataAnnotations();

        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient<INlToSqlClient, DeepSeekNlToSqlClient>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<AssistantOptions>>().Value;
            // Outlives the per-request timeout the client applies itself, so the cancellation
            // that fires is the one carrying a useful message.
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds * 2);
        });

        services.AddSingleton<ISqlSafetyValidator, SqlSafetyValidator>();

        // Singleton so the schema is read once rather than per question — it cannot change under
        // a running process.
        services.AddSingleton<ISchemaContextProvider, SchemaContextProvider>();
        services.AddScoped<ReadOnlySqlExecutor>();
        services.AddScoped<IAssistantService, AssistantService>();

        return services;
    }
}
