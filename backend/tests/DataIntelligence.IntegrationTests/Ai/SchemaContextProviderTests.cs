using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Exceptions;
using DataIntelligence.Infrastructure.Ai;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
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
    public async Task ReusesTheStructuralDescriptionBetweenQuestions()
    {
        var provider = Build();

        var first = await provider.GetContextAsync(default);
        var second = await provider.GetContextAsync(default);

        // The structural half is cached; the temporal half is not, so the whole string is not
        // reference-equal. What must hold is that the view descriptions are identical.
        var firstStructure = first[..first.IndexOf("Today's date is", StringComparison.Ordinal)];
        var secondStructure = second[..second.IndexOf("Today's date is", StringComparison.Ordinal)];

        Assert.Equal(firstStructure, secondStructure);
    }

    // ------------------------------------------------------------------ "now"

    [Fact]
    public async Task TellsTheModelTodaysDate()
    {
        // Without this, "the average SOFR rate last month" is unanswerable — not because the query
        // is hard, but because "last month" cannot be resolved. The model then refuses, which is
        // the right failure and a useless answer.
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 6, 9, 30, 0, TimeSpan.Zero));

        var context = await Build(clock).GetContextAsync(default);

        Assert.Contains("Today's date is 2026-08-06", context);
    }

    [Fact]
    public async Task MovesTodaysDateWithTheClockRatherThanPinningItAtStartup()
    {
        // A process up since yesterday would otherwise answer "last month" against yesterday's
        // idea of the calendar, and keep doing so until it was restarted.
        //
        // The instants below straddle midnight in PAKISTAN, not in UTC, which is the point: 18:59
        // UTC is 23:59 the same day in Karachi, and 19:01 UTC is already the next morning. A
        // provider that still resolved "today" against UTC would report the 6th for both and be
        // wrong for five hours of every day — the window in which "last month" and "year to date"
        // quietly shift by one.
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 6, 18, 59, 0, TimeSpan.Zero));
        var provider = Build(clock);

        Assert.Contains("Today's date is 2026-08-06", await provider.GetContextAsync(default));

        clock.Now = new DateTimeOffset(2026, 8, 6, 19, 1, 0, TimeSpan.Zero);

        Assert.Contains("Today's date is 2026-08-07", await provider.GetContextAsync(default));
    }

    [Fact]
    public async Task StatesWhatTheDatabaseActuallyHolds()
    {
        // The newest CPI figure is normally weeks behind today, so "last month" and "the most
        // recent month with data" are often different months. A model told only the date would
        // confidently produce an empty result.
        await SeedCpiAsync();

        var context = await Build().GetContextAsync(default);

        Assert.Contains("What the database currently holds", context);
        Assert.Contains("analytics.vw_Cpi", context);
    }

    [Fact]
    public async Task SaysNothingAboutCoverageWhenNothingHasBeenCollected()
    {
        // An empty database must not claim a range. Every row here is deleted first.
        await ExecuteAsync("DELETE FROM core.CpiObservation;");

        var context = await Build().GetContextAsync(default);

        // Still a usable context — the structural half is what the model needs to write SQL.
        Assert.Contains("analytics.vw_Cpi(", context);
        Assert.Contains("Today's date is", context);
    }

    [Fact]
    public async Task SaysSoWhenTheReadModelsAreMissingEntirely()
    {
        // A database that has had the migrations but not section 5 of the schema script. Without
        // this the model would be handed an empty view list and asked to write SQL anyway.
        var provider = new SchemaContextProvider(
            Configuration(_fixture.ConnectionString.Replace(_fixture.DatabaseName, "master")),
            TimeProvider.System);

        await Assert.ThrowsAsync<AssistantNotConfiguredException>(
            () => provider.GetContextAsync(default));
    }

    [Fact]
    public async Task SaysSoWhenThereIsNoConnectionStringAtAll()
    {
        var provider = new SchemaContextProvider(
            new ConfigurationBuilder().AddInMemoryCollection([]).Build(),
            TimeProvider.System);

        var ex = await Assert.ThrowsAsync<AssistantNotConfiguredException>(
            () => provider.GetContextAsync(default));

        Assert.Contains("DataIntelligenceDb", ex.Message);
    }

    private SchemaContextProvider Build(TimeProvider? clock = null) =>
        new(Configuration(_fixture.ConnectionString), clock ?? TimeProvider.System);

    /// <summary>
    /// One CPI figure, so the coverage window has something to report. Written through the entity
    /// model rather than raw SQL — the table has enough NOT NULL columns that a hand-written
    /// INSERT is a list of surprises, and the defaults live on the entity anyway.
    /// </summary>
    private async Task SeedCpiAsync()
    {
        await using var db = _fixture.CreateContext();

        if (await db.CpiObservations.AnyAsync())
        {
            return;
        }

        var run = new CollectionRun
        {
            DataSourceId = 1,
            ScheduledForPkt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            StartedAtPkt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            Status = CollectionRunStatus.Succeeded,
            TriggerType = CollectionTriggerType.Manual,
            RequestUrl = "https://api.bls.gov/publicAPI/v2/timeseries/data/",
        };

        db.CollectionRuns.Add(run);
        await db.SaveChangesAsync();

        db.CpiObservations.Add(new CpiObservation
        {
            SeriesCode = "CUUR0000SA0",
            ReferenceDate = new DateOnly(2025, 6, 1),
            ReferenceYear = 2025,
            PeriodCode = "M06",
            PeriodType = PeriodType.Month,
            IndexValue = 322.561m,
            CollectionRunId = run.CollectionRunId,
            RowHash = new byte[32],
        });

        await db.SaveChangesAsync();
    }

    /// <summary>A clock the test moves, so "today" can be asserted without waiting for midnight.</summary>
    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

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
