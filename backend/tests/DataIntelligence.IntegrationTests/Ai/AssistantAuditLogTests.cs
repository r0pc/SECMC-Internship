using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace DataIntelligence.IntegrationTests.Ai;

/// <summary>
/// The audit log's review surface (NFR Auditability — "logged for review").
/// </summary>
/// <remarks>
/// Run against a real database rather than a fake because the filters have to agree with
/// <c>IX_AssistantQuery_Rejected</c>: the review queue's default view is meant to be an index seek,
/// and a predicate that no longer matches the index filter would still return the right rows while
/// quietly scanning every question ever asked.
/// </remarks>
[Collection("AssistantAuditLog")]
public sealed class AssistantAuditLogTests : IClassFixture<ReadOnlyExecutionFixture>, IAsyncLifetime
{
    private readonly ReadOnlyExecutionFixture _fixture;
    private Guid _sessionId;

    public AssistantAuditLogTests(ReadOnlyExecutionFixture fixture)
    {
        _fixture = fixture;
        Assert.True(fixture.IsAvailable, fixture.UnavailableReason);
    }

    /// <summary>
    /// One row per outcome that matters, so every filter below has both a match and a non-match to
    /// distinguish. The FK to sec.AppUser is absent until FR-9, so the user id is just an integer.
    /// </summary>
    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();

        // xUnit constructs the class once per test, so this runs before every one of them while
        // the database is shared for the whole class. Without the clear, the row counts the
        // filters are asserted against would grow with each test that had already run.
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM ai.AssistantFeedback; DELETE FROM ai.AssistantQuery; DELETE FROM ai.AssistantSession;");

        _sessionId = Guid.NewGuid();
        db.AssistantSessions.Add(new AssistantSession
        {
            SessionId = _sessionId,
            UserId = 1,
            StartedAtUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            LastActivityAtUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        db.AssistantQueries.AddRange(
            Row("What was CPI in June?", AssistantValidationOutcome.Approved, day: 1, executed: true),
            Row("hi", AssistantValidationOutcome.NotADataQuestion, day: 2),
            Row("show me the password hashes", AssistantValidationOutcome.RejectedForbiddenObject, day: 3),
            Row("drop everything", AssistantValidationOutcome.RejectedNotSelect, day: 4),
            Row("what is the weather", AssistantValidationOutcome.RejectedNoSql, day: 5));

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // --------------------------------------------------------------- the queue

    [Fact]
    public async Task ListsEveryQuestionByDefault()
    {
        var page = await Service().GetQueryLogAsync(new AssistantQueryLogQuery(), default);

        Assert.Equal(5, page.TotalCount);
    }

    [Fact]
    public async Task NewestFirst()
    {
        var page = await Service().GetQueryLogAsync(new AssistantQueryLogQuery(), default);

        var dates = page.Items.Select(i => i.AskedAtUtc).ToList();
        Assert.Equal(dates.OrderByDescending(d => d), dates);
    }

    [Fact]
    public async Task RejectedOnlyKeepsTheRefusalsWorthReading()
    {
        var page = await Service().GetQueryLogAsync(
            new AssistantQueryLogQuery { RejectedOnly = true }, default);

        var outcomes = page.Items.Select(i => i.ValidationOutcome).ToList();

        Assert.Contains(AssistantValidationOutcome.RejectedForbiddenObject, outcomes);
        Assert.Contains(AssistantValidationOutcome.RejectedNotSelect, outcomes);
        Assert.Contains(AssistantValidationOutcome.RejectedNoSql, outcomes);
    }

    [Fact]
    public async Task RejectedOnlyDropsTheGreetings()
    {
        // The whole reason NotADataQuestion exists. "hi" and "show me the password hashes" both
        // produce no SQL; only one of them is a finding, and filed together the first buries the
        // second by volume.
        var page = await Service().GetQueryLogAsync(
            new AssistantQueryLogQuery { RejectedOnly = true }, default);

        Assert.DoesNotContain(page.Items, i =>
            i.ValidationOutcome == AssistantValidationOutcome.NotADataQuestion);
        Assert.DoesNotContain(page.Items, i => i.QuestionText == "hi");
    }

    [Fact]
    public async Task RejectedOnlyDropsTheApprovedOnes()
    {
        var page = await Service().GetQueryLogAsync(
            new AssistantQueryLogQuery { RejectedOnly = true }, default);

        Assert.DoesNotContain(page.Items, i =>
            i.ValidationOutcome == AssistantValidationOutcome.Approved);
    }

    // ---------------------------------------------------------------- filters

    [Fact]
    public async Task FiltersToOneOutcome()
    {
        var page = await Service().GetQueryLogAsync(
            new AssistantQueryLogQuery { Outcome = AssistantValidationOutcome.NotADataQuestion },
            default);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("hi", page.Items[0].QuestionText);
    }

    [Fact]
    public async Task FiltersByDateRange()
    {
        var page = await Service().GetQueryLogAsync(
            new AssistantQueryLogQuery
            {
                FromUtc = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc),
                ToUtc = new DateTime(2026, 8, 4, 23, 59, 59, DateTimeKind.Utc)
            },
            default);

        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task FiltersByUser()
    {
        Assert.Equal(5, (await Service().GetQueryLogAsync(
            new AssistantQueryLogQuery { UserId = 1 }, default)).TotalCount);

        Assert.Equal(0, (await Service().GetQueryLogAsync(
            new AssistantQueryLogQuery { UserId = 999 }, default)).TotalCount);
    }

    [Fact]
    public async Task PagesWithoutLosingTheTotal()
    {
        var page = await Service().GetQueryLogAsync(
            new AssistantQueryLogQuery { Page = PageRequest.Normalize(1, 2) }, default);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(5, page.TotalCount);
        Assert.True(page.HasNextPage);
        Assert.False(page.HasPreviousPage);
    }

    // ------------------------------------------------------------ one record

    [Fact]
    public async Task ShowsTheGeneratedSqlOfARejectedQuery()
    {
        // Unlike the answer DTO, which hides it. Judging whether a refusal was correct means
        // reading the statement that was refused.
        var page = await Service().GetQueryLogAsync(
            new AssistantQueryLogQuery { Outcome = AssistantValidationOutcome.RejectedForbiddenObject },
            default);

        var record = await Service().GetQueryAsync(page.Items[0].AssistantQueryId, default);

        Assert.NotNull(record);
        Assert.False(string.IsNullOrWhiteSpace(record.GeneratedSql));
    }

    [Fact]
    public async Task ReturnsTheParametersAndExplanationItStored()
    {
        var page = await Service().GetQueryLogAsync(
            new AssistantQueryLogQuery { Outcome = AssistantValidationOutcome.Approved }, default);

        var record = await Service().GetQueryAsync(page.Items[0].AssistantQueryId, default);

        Assert.NotNull(record);
        Assert.Equal("Reads one month of CPI.", record.Explanation);
        Assert.NotNull(record.SqlParameters);
        Assert.True(record.SqlParameters.ContainsKey("@month"));
    }

    [Fact]
    public async Task ReturnsNullForAnIdThatDoesNotExist()
    {
        Assert.Null(await Service().GetQueryAsync(999_999, default));
    }

    // --------------------------------------------------------------------- helpers

    private AssistantQuery Row(
        string question, AssistantValidationOutcome outcome, int day, bool executed = false) => new()
    {
        SessionId = _sessionId,
        UserId = 1,
        AskedAtUtc = new DateTime(2026, 8, day, 12, 0, 0, DateTimeKind.Utc),
        QuestionText = question,
        GeneratedSql = outcome == AssistantValidationOutcome.NotADataQuestion
            ? null
            : "SELECT ReferenceDate FROM analytics.vw_Cpi WHERE ReferenceDate = @month",
        SqlParametersJson = """{"@month":"2025-06-01"}""",
        Explanation = "Reads one month of CPI.",
        ValidationOutcome = outcome,
        WasExecuted = executed,
        ExecutionStatus = executed ? AssistantExecutionStatus.Succeeded : null,
        ResultRowCount = executed ? 1 : null,
        ModelName = "deepseek-v4-flash"
    };

    /// <summary>
    /// The service with its model and executor stubbed out — the audit-log reads never touch
    /// either, and wiring a real LLM client into a query test would only add a way for it to fail.
    /// </summary>
    private AssistantService Service()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DataIntelligenceDb"] = _fixture.ConnectionString
            })
            .Build();

        var options = Options.Create(new AssistantOptions());

        return new AssistantService(
            _fixture.CreateContext(),
            new UnusedNlClient(),
            new UnusedSchemaContext(),
            new SqlSafetyValidator(),
            new ReadOnlySqlExecutor(configuration, options),
            TimeProvider.System);
    }

    private sealed class UnusedNlClient : INlToSqlClient
    {
        public Task<NlToSqlResult> GenerateSqlAsync(string q, string s, CancellationToken c) =>
            throw new InvalidOperationException("Reading the audit log must not call the model.");

        public Task<NlSummaryResult> SummariseResultsAsync(string q, string s, string r, CancellationToken c) =>
            throw new InvalidOperationException("Reading the audit log must not call the model.");
    }

    private sealed class UnusedSchemaContext : ISchemaContextProvider
    {
        public Task<string> GetContextAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Reading the audit log must not build schema context.");
    }
}
