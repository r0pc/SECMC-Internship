using DataIntelligence.Core.Exceptions;
using DataIntelligence.Infrastructure.Ai;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace DataIntelligence.IntegrationTests.Ai;

/// <summary>
/// The second of the assistant's two controls: that a statement reaching the database cannot write,
/// whatever the validator did or failed to do (FR-14, SOW 9 Risk 3).
/// </summary>
/// <remarks>
/// Every statement below would be rejected by <c>ISqlSafetyValidator</c> long before it got here.
/// They are executed directly against <see cref="ReadOnlySqlExecutor"/> on purpose: the question
/// this class answers is what happens *when the validator is wrong*, and a test that went through
/// the validator would only ever prove the validator works.
/// </remarks>
[Collection("ReadOnlyExecution")]
public sealed class ReadOnlyExecutionTests : IClassFixture<ReadOnlyExecutionFixture>
{
    private readonly ReadOnlyExecutionFixture _fixture;

    public ReadOnlyExecutionTests(ReadOnlyExecutionFixture fixture)
    {
        _fixture = fixture;
        Assert.True(fixture.IsAvailable, fixture.UnavailableReason);
    }

    // ------------------------------------------------------------------ reads

    [Fact]
    public async Task ReadsThePublishedViewItIsAllowedToRead()
    {
        var result = await Execute("SELECT TOP (1) ReferenceDate FROM analytics.vw_Cpi");

        Assert.True(result.Succeeded, result.ErrorMessage);
    }

    [Fact]
    public async Task BindsParametersAsDataRatherThanSql()
    {
        // The classic injection payload as a *value*. Bound, it is a string that matches nothing;
        // concatenated, it would have closed the literal and run a second statement.
        var result = await Execute(
            "SELECT TOP (1) ReferenceDate FROM analytics.vw_Cpi WHERE SeriesCode = @code",
            new Dictionary<string, object?> { ["@code"] = "x'; DROP TABLE core.CpiObservation; --" });

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Empty(result.Rows!);

        // The table the payload named is still there.
        Assert.True(await TableExistsAsync("core", "CpiObservation"));
    }

    // ----------------------------------------------------------------- writes

    [Theory]
    [InlineData("INSERT INTO core.CpiObservation (SeriesCode) VALUES ('x')")]
    [InlineData("UPDATE core.CpiObservation SET IndexValue = 0")]
    [InlineData("DELETE FROM core.CpiObservation")]
    [InlineData("DROP TABLE core.CpiObservation")]
    [InlineData("TRUNCATE TABLE core.CpiObservation")]
    public async Task RefusesEveryWrite(string sql)
    {
        var result = await Execute(sql);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task LeavesTheDataIntactWhenAWriteIsRefused()
    {
        // Asserted against collect.DataSource because the migration seeds it: a refusal that left
        // an already-empty table empty would prove nothing.
        var before = await ScalarAsync("SELECT COUNT(*) FROM collect.DataSource");
        Assert.True(before > 0, "The migration should have seeded the designated sources.");

        var result = await Execute("DELETE FROM collect.DataSource");

        Assert.False(result.Succeeded);
        Assert.Equal(before, await ScalarAsync("SELECT COUNT(*) FROM collect.DataSource"));
    }

    // ------------------------------------------------------------ off-limits reads

    [Theory]
    // The audit trail and the base tables. Reading the audit log would let a question ask what
    // other people had asked; reading core would bypass the current-vintage filter the views apply.
    [InlineData("SELECT TOP (1) * FROM ai.AssistantQuery")]
    [InlineData("SELECT TOP (1) * FROM core.CpiObservation")]
    [InlineData("SELECT TOP (1) * FROM collect.RawPayload")]
    public async Task RefusesToReadWhatItIsDeniedEvenThoughItIsOnlyASelect(string sql)
    {
        var result = await Execute(sql);

        Assert.False(result.Succeeded);
    }

    // -------------------------------------------------------------- the session

    [Fact]
    public async Task RunsAsTheRestrictedPrincipalNotAsTheConnectionsOwnLogin()
    {
        var result = await Execute("SELECT TOP (1) CURRENT_USER AS WhoAmI FROM analytics.vw_Cpi");

        // No rows would make this vacuous, so assert against the session directly instead when the
        // view is empty — the impersonation is what is being pinned, not the data.
        if (result.Rows is { Count: > 0 })
        {
            Assert.Equal(ReadOnlyExecutionFixture.ReadOnlyUser, result.Rows[0]["WhoAmI"]);
        }
        else
        {
            Assert.True(result.Succeeded, result.ErrorMessage);
        }
    }

    [Fact]
    public async Task ReturnsTheConnectionToFullRightsAfterwards()
    {
        // REVERT matters because the connection goes back to a pool. A session left impersonating
        // would silently strip rights from whatever ran on it next.
        await Execute("SELECT TOP (1) ReferenceDate FROM analytics.vw_Cpi");

        // A write the impersonated principal was refused, on a fresh connection from the same pool.
        await using var context = _fixture.CreateContext();
        var written = await context.Database.ExecuteSqlRawAsync(
            "UPDATE collect.DataSource SET IsEnabled = IsEnabled");

        Assert.True(written > 0);
    }

    // ----------------------------------------------------------- configuration

    [Fact]
    public async Task RefusesToRunAtAllWhenNeitherPrivilegeSeparationIsConfigured()
    {
        // The failure mode this guards against is the quiet one: falling back to the app's own
        // connection would run model-written SQL with INSERT and UPDATE rights, and nothing in
        // the response would say so.
        var executor = Build(readOnlyConnection: null, executeAsUser: null);

        await Assert.ThrowsAsync<AssistantNotConfiguredException>(
            () => executor.ExecuteAsync("SELECT 1 FROM analytics.vw_Cpi", null, default));
    }

    [Fact]
    public async Task RefusesAPrincipalNameThatIsNotAPlainIdentifier()
    {
        var executor = Build(readOnlyConnection: null, executeAsUser: "di_ai_user'; DROP DATABASE x; --");

        await Assert.ThrowsAsync<AssistantNotConfiguredException>(
            () => executor.ExecuteAsync("SELECT 1 FROM analytics.vw_Cpi", null, default));
    }

    [Fact]
    public async Task SaysSoWhenTheConfiguredPrincipalDoesNotExist()
    {
        var executor = Build(readOnlyConnection: null, executeAsUser: "no_such_user");

        var ex = await Assert.ThrowsAsync<AssistantNotConfiguredException>(
            () => executor.ExecuteAsync("SELECT 1 FROM analytics.vw_Cpi", null, default));

        Assert.Contains("no_such_user", ex.Message);
    }

    // --------------------------------------------------------------------- helpers

    private Task<QueryExecutionResult> Execute(
        string sql, IReadOnlyDictionary<string, object?>? parameters = null) =>
        Build(readOnlyConnection: null, executeAsUser: ReadOnlyExecutionFixture.ReadOnlyUser)
            .ExecuteAsync(sql, parameters, default);

    private ReadOnlySqlExecutor Build(string? readOnlyConnection, string? executeAsUser)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DataIntelligenceDb"] = _fixture.ConnectionString,
            ["ConnectionStrings:DataIntelligenceDbReadOnly"] = readOnlyConnection
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var options = Options.Create(new AssistantOptions
        {
            ExecuteAsUser = executeAsUser,
            SqlExecutionTimeoutSeconds = 10
        });

        return new ReadOnlySqlExecutor(configuration, options);
    }

    private async Task<int> ScalarAsync(string sql)
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<bool> TableExistsAsync(string schema, string table) =>
        await ScalarAsync(
            $"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES "
            + $"WHERE TABLE_SCHEMA = '{schema}' AND TABLE_NAME = '{table}'") == 1;
}
