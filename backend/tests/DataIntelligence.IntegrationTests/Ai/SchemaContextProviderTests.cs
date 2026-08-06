using DataIntelligence.Core.Exceptions;
using DataIntelligence.Infrastructure.Ai;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace DataIntelligence.IntegrationTests.Ai;

/// <summary>
/// That the schema the model is shown comes from the database rather than from a hand-kept list
/// (FR-13).
/// </summary>
/// <remarks>
/// The point of deriving it is that it cannot drift. A hand-written description goes stale the
/// first time a view gains a column, and the failure that produces is the quiet kind — the model
/// writes a query naming a column that no longer exists, or never learns about one that now does,
/// and the user gets a worse answer rather than an error. These tests change the database and
/// assert the description follows.
/// </remarks>
[Collection("SchemaContext")]
public sealed class SchemaContextProviderTests : IClassFixture<ReadOnlyExecutionFixture>
{
    private readonly ReadOnlyExecutionFixture _fixture;

    public SchemaContextProviderTests(ReadOnlyExecutionFixture fixture)
    {
        _fixture = fixture;
        Assert.True(fixture.IsAvailable, fixture.UnavailableReason);
    }

    [Fact]
    public async Task DescribesTheViewWithTheColumnsItActuallyHas()
    {
        var context = await Build().GetContextAsync(default);

        Assert.Contains("analytics.vw_Cpi(", context);

        // Every column the fixture's view declares, named exactly.
        foreach (var column in new[]
                 { "ReferenceDate", "ReferenceYear", "PeriodCode", "SeriesCode", "IndexValue" })
        {
            Assert.Contains(column, context);
        }
    }

    [Fact]
    public async Task PicksUpAColumnAddedAfterTheProcessWasWritten()
    {
        // The drift case, made concrete: a view changes, and the description changes with it
        // without anyone editing a string.
        var viewName = $"vw_Cpi";
        await ExecuteAsync($"""
            DROP VIEW analytics.{viewName};
            EXEC('CREATE VIEW analytics.{viewName} AS
                  SELECT ReferenceDate, ReferenceYear, PeriodCode, SeriesCode, IndexValue,
                         RevisionNumber
                  FROM core.CpiObservation WHERE IsCurrent = 1');
            """);

        try
        {
            // A fresh provider: the cache is per-instance and deliberately never invalidated,
            // because the schema cannot change under a running API.
            var context = await Build().GetContextAsync(default);

            Assert.Contains("RevisionNumber", context);
        }
        finally
        {
            await ExecuteAsync($"""
                DROP VIEW analytics.{viewName};
                EXEC('CREATE VIEW analytics.{viewName} AS
                      SELECT ReferenceDate, ReferenceYear, PeriodCode, SeriesCode, IndexValue
                      FROM core.CpiObservation WHERE IsCurrent = 1');
                """);
        }
    }

    [Fact]
    public async Task NeverDescribesAViewTheValidatorWouldRefuse()
    {
        // A view added to analytics without being added to the allow-list would otherwise be
        // described to the model and then refused when it used it — which reads to the user as
        // the assistant being broken rather than as the view being off-limits.
        await ExecuteAsync(
            "EXEC('CREATE VIEW analytics.vw_NotOnTheList AS SELECT 1 AS X');");

        try
        {
            var context = await Build().GetContextAsync(default);

            Assert.DoesNotContain("vw_NotOnTheList", context);
        }
        finally
        {
            await ExecuteAsync("DROP VIEW analytics.vw_NotOnTheList;");
        }
    }

    [Fact]
    public async Task KeepsTheSemanticsColumnMetadataCannotSupply()
    {
        var context = await Build().GetContextAsync(default);

        // None of this is derivable, and every line of it was added because the model got
        // something wrong without it.
        Assert.Contains("M13", context);            // the annual average is not a thirteenth month
        Assert.Contains("TOP (n)", context);        // the dialect is T-SQL, not MySQL
        Assert.Contains("CUUR0000SA0", context);    // the real series code, not a plausible guess
    }

    [Fact]
    public async Task ReadsTheSchemaOnceAndReusesIt()
    {
        var provider = Build();

        var first = await provider.GetContextAsync(default);
        var second = await provider.GetContextAsync(default);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task SaysSoWhenTheReadModelsAreMissingEntirely()
    {
        // A database that has had the migrations but not section 5 of the schema script. Without
        // this the model would be handed an empty view list and asked to write SQL anyway.
        var provider = new SchemaContextProvider(Configuration(_fixture.ConnectionString
            .Replace(_fixture.DatabaseName, "master")));

        await Assert.ThrowsAsync<AssistantNotConfiguredException>(
            () => provider.GetContextAsync(default));
    }

    [Fact]
    public async Task SaysSoWhenThereIsNoConnectionStringAtAll()
    {
        var provider = new SchemaContextProvider(
            new ConfigurationBuilder().AddInMemoryCollection([]).Build());

        var ex = await Assert.ThrowsAsync<AssistantNotConfiguredException>(
            () => provider.GetContextAsync(default));

        Assert.Contains("DataIntelligenceDb", ex.Message);
    }

    private SchemaContextProvider Build() =>
        new(Configuration(_fixture.ConnectionString));

    private static IConfiguration Configuration(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DataIntelligenceDb"] = connectionString
            })
            .Build();

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
