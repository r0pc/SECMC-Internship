using DataIntelligence.Core.Interfaces;
using DataIntelligence.Infrastructure;
using DataIntelligence.Infrastructure.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DataIntelligence.UnitTests.Ai;

/// <summary>
/// That everything the assistant needs can actually be constructed.
/// </summary>
/// <remarks>
/// This exists because of a failure nothing else here could see. `AssistantPlanCache` took an
/// <c>IMemoryCache</c>, and <c>AddMemoryCache()</c> was called in <c>AddCollection</c> — which the
/// Worker calls and the API does not. It compiled, every unit test passed, and the API then refused
/// to start: a missing registration is invisible to the compiler, and invisible to a test that
/// constructs its subject by hand.
/// <para>
/// So this builds the container the API builds and validates it, which is exactly what the host does
/// on startup. It needs no database and no model — the registrations are resolved, not used — so it
/// costs a few milliseconds to close a gap that otherwise only shows up when someone runs the API.
/// </para>
/// </remarks>
public class AssistantRegistrationTests
{
    /// <summary>The registrations the API makes, in the order Program.cs makes them.</summary>
    private static ServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DataIntelligenceDb"] =
                    "Server=localhost;Database=none;Trusted_Connection=True;",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Both of these the real host adds for you before any of our extension methods run, so
        // production never has to. A bare ServiceCollection has neither, and without them this test
        // fails on its own scaffolding rather than on the registrations it is here to check.
        services.AddSingleton<IConfiguration>(configuration);

        services.AddInfrastructure(configuration);
        services.AddAnalytics();
        services.AddAssistant(configuration);

        // Both the validations the default host performs in Development, which is what turns a
        // missing or mis-scoped registration into a startup failure rather than a runtime surprise.
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void EveryAssistantServiceCanBeConstructed()
    {
        using var provider = Build();

        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAssistantService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AssistantPlanCache>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<INlToSqlClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISchemaContextProvider>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISqlSafetyValidator>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ReadOnlySqlExecutor>());
    }

    [Fact]
    public void ThePlanCacheIsSharedRatherThanRebuiltPerRequest()
    {
        // A cache built per request would never see a second question, so every lookup would miss
        // and the whole thing would be a lookup that costs and never pays. Two scopes must get the
        // same instance.
        using var provider = Build();

        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<AssistantPlanCache>(),
            second.ServiceProvider.GetRequiredService<AssistantPlanCache>());
    }
}
