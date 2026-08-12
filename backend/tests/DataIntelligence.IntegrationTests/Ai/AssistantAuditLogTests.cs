using DataIntelligence.Core.Dtos;
using DataIntelligence.Infrastructure.Persistence;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.IntegrationTests.Api;
using DataIntelligence.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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
    /// <summary>The account every seeded question is asked by, and every filter below asserts on.</summary>
    private const int AskingUserId = 1;

    private readonly ReadOnlyExecutionFixture _fixture;
    private Guid _sessionId;

    public AssistantAuditLogTests(ReadOnlyExecutionFixture fixture)
    {
        _fixture = fixture;
        Assert.True(fixture.IsAvailable, fixture.UnavailableReason);
    }

    /// <summary>
    /// One row per outcome that matters, so every filter below has both a match and a non-match to
    /// distinguish.
    /// </summary>
    /// <remarks>
    /// The account is seeded first and with a forced id, because <c>ai.AssistantSession.UserId</c>
    /// carries a foreign key to <c>sec.AppUser</c> as of FR-9. These tests assert on user 1
    /// specifically, so user 1 is what gets created rather than whatever the identity column would
    /// have allocated.
    /// </remarks>
    public async Task InitializeAsync()
    {
        await using var db = _fixture.CreateContext();

        await TestAccounts.EnsureWithIdAsync(db, AskingUserId, "Audit log test user");

        // xUnit constructs the class once per test, so this runs before every one of them while
        // the database is shared for the whole class. Without the clear, the row counts the
        // filters are asserted against would grow with each test that had already run.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM ai.AssistantSession;");

        _sessionId = Guid.NewGuid();

        var session = new AssistantSession
        {
            SessionId = _sessionId,
            UserId = AskingUserId,
            StartedAtPkt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            LastActivityAtPkt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        // Seeded as the transcript document rather than as rows, because that is now the store —
        // and going through ChatTranscriptWriter rather than hand-writing JSON means these tests
        // read the audit log out of exactly the shape the service writes. A fixture that spelled
        // the document itself could drift from the writer and still pass.
        session.TranscriptJson = ChatTranscriptWriter.Serialize(session,
        [
            Turn(1, "What was CPI in June?", AssistantValidationOutcome.Approved, day: 1,
                executed: true, model: AssistantModelChoice.Local),

            // Left without one on purpose: the queue has to keep reading turns recorded before the
            // model became a choice, and those say nothing about which one answered.
            Turn(2, "hi", AssistantValidationOutcome.NotADataQuestion, day: 2),
            Turn(3, "show me the password hashes", AssistantValidationOutcome.RejectedForbiddenObject, day: 3),
            Turn(4, "drop everything", AssistantValidationOutcome.RejectedNotSelect, day: 4),
            Turn(5, "what is the weather", AssistantValidationOutcome.RejectedNoSql, day: 5)
        ]);

        // What AssistantService.SaveTurnsAsync would have written: the turns' own totals added up.
        // Seeded rather than computed here so the assertion below has something to disagree with —
        // a column that the test derived the same way the code does could not catch the code
        // getting it wrong.
        session.TotalTokens = 5 * 429;

        db.AssistantSessions.Add(session);

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

        var dates = page.Items.Select(i => i.AskedAtPkt).ToList();
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
            new AssistantQueryLogQuery { UserId = AskingUserId }, default)).TotalCount);

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

    // ------------------------------------------------------- which model answered

    [Fact]
    public async Task ReadsBackWhichModelAnsweredEachTurn()
    {
        // The round trip that matters for the choice: written into the transcript document as a
        // name, shredded back out by OPENJSON, converted to the enum. The path strings in
        // ConfigureAssistantTurn are a contract with ChatTranscriptWriter's camelCase output, and
        // OPENJSON reports a mismatched one as NULL rather than as an error — so a broken path
        // here looks exactly like a turn that never recorded a model, and only a test catches it.
        var page = await Service().GetQueryLogAsync(
            new AssistantQueryLogQuery { Outcome = AssistantValidationOutcome.Approved }, default);

        var record = await Service().GetQueryAsync(page.Items[0].AssistantQueryId, default);

        Assert.NotNull(record);
        Assert.Equal(AssistantModelChoice.Local, record.ModelChoice);
        Assert.Equal("qwen3.5:2b", record.ModelName);
    }

    [Fact]
    public async Task ReportsNoModelForATurnRecordedBeforeThereWasAChoice()
    {
        // A document store has no migration step, so older turns simply lack the field and
        // OPENJSON returns NULL. Null is the honest answer — those turns reached the only gateway
        // there was, but the record does not say so, and defaulting them to Cloud would assert a
        // fact nobody wrote down.
        var page = await Service().GetQueryLogAsync(
            new AssistantQueryLogQuery { Outcome = AssistantValidationOutcome.NotADataQuestion },
            default);

        var record = await Service().GetQueryAsync(page.Items[0].AssistantQueryId, default);

        Assert.NotNull(record);
        Assert.Null(record.ModelChoice);
    }

    // ------------------------------------------------------- what a chat cost

    [Fact]
    public async Task ListsWhatEachConversationCostInTokens()
    {
        var chat = Assert.Single(await Service().GetSessionsAsync(AskingUserId, limit: 10, default));

        // Cross-checked against the turn view rather than asserted as a bare number: the session
        // column is a denormalisation of the transcript, and it is worth having only while it says
        // the same thing the turns do.
        var turns = await Service().GetQueryLogAsync(new AssistantQueryLogQuery(), default);

        Assert.Equal(turns.Items.Sum(t => t.TotalTokens ?? 0), chat.TotalTokens);
    }

    [Fact]
    public async Task ReportsNoTokenTotalForAConversationThatNeverRecordedOne()
    {
        // Null, not zero. A transcript written before usage was recorded — or one whose turns the
        // provider returned no usage for — is a conversation whose cost is unknown, and showing it
        // as free would be a claim the data does not support.
        await using (var db = _fixture.CreateContext())
        {
            var session = new AssistantSession
            {
                SessionId = Guid.NewGuid(),
                UserId = AskingUserId,
                StartedAtPkt = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc),
                LastActivityAtPkt = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc)
            };

            session.TranscriptJson = ChatTranscriptWriter.Serialize(session,
            [
                new ChatTranscriptTurn
                {
                    AssistantQueryId = 99,
                    AskedAtPkt = new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc),
                    Question = "What was CPI in May?",
                    Outcome = AssistantValidationOutcome.Approved
                }
            ]);

            db.AssistantSessions.Add(session);
            await db.SaveChangesAsync();
        }

        var sessions = await Service().GetSessionsAsync(AskingUserId, limit: 10, default);

        Assert.Null(Assert.Single(sessions, s => s.Title == "What was CPI in May?").TotalTokens);
    }

    // --------------------------------------------------------------------- helpers

    /// <param name="model">
    /// Which model answered, or null for a turn written before there was a choice — the shape of
    /// every transcript already in the table when this landed, and one the audit log has to keep
    /// reading.
    /// </param>
    private static ChatTranscriptTurn Turn(
        long id,
        string question,
        AssistantValidationOutcome outcome,
        int day,
        bool executed = false,
        AssistantModelChoice? model = null) => new()
    {
        AssistantQueryId = id,
        AskedAtPkt = new DateTime(2026, 8, day, 12, 0, 0, DateTimeKind.Utc),
        Question = question,
        Sql = outcome == AssistantValidationOutcome.NotADataQuestion
            ? null
            : "SELECT ReferenceDate FROM analytics.vw_Cpi WHERE ReferenceDate = @month",
        Parameters = new Dictionary<string, object?> { ["@month"] = "2025-06-01" },
        Explanation = "Reads one month of CPI.",
        Outcome = outcome,
        WasExecuted = executed,
        ExecutionStatus = executed ? AssistantExecutionStatus.Succeeded : null,
        ResultRowCount = executed ? 1 : null,
        ModelChoice = model,
        ModelName = model == AssistantModelChoice.Local ? "qwen3.5:2b" : "deepseek-v4-flash",
        PromptTokens = 412,
        CompletionTokens = 17,
        TotalTokens = 429
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
            new AssistantPlanCache(new MemoryCache(new MemoryCacheOptions())),
            TimeProvider.System,
            options);
    }

    private sealed class UnusedNlClient : INlToSqlClient
    {
        public Task<NlToSqlResult> GenerateSqlAsync(
            string q, string s, IReadOnlyList<ConversationTurn> h, AssistantModelChoice m,
            CancellationToken c) =>
            throw new InvalidOperationException("Reading the audit log must not call the model.");

        public Task<NlSummaryResult> SummariseResultsAsync(
            string q, string s, IReadOnlyDictionary<string, object?> p, string r, string cov,
            AssistantModelChoice m, CancellationToken c) =>
            throw new InvalidOperationException("Reading the audit log must not call the model.");
    }

    private sealed class UnusedSchemaContext : ISchemaContextProvider
    {
        public Task<string> GetContextAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Reading the audit log must not build schema context.");

        public Task<string> GetCoverageAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Reading the audit log must not read coverage.");
    }
}
