using System.Net;
using System.Text;
using System.Text.Json;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Exceptions;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace DataIntelligence.UnitTests.Ai;

/// <summary>
/// Prompt building and response parsing for the NL-to-SQL client (SOW 11.1 names this as unit-test
/// scope). The transport is stubbed throughout — no test here reaches a live API.
/// </summary>
/// <remarks>
/// What is being pinned is the client's tolerance of a model that does not follow instructions
/// exactly. A model wrapping its JSON in a code fence, or answering with prose, must degrade into
/// a rejected query the assistant can explain — never an unhandled exception on the request path.
/// <para>
/// One class covers both providers because one class serves both: everything about a question is
/// identical whichever model answers it, and the section at the bottom pins the handful of things
/// that are not — where the request goes, whether it carries a key, and what the two failures
/// unique to a local server are reported as.
/// </para>
/// </remarks>
public class ChatCompletionsNlToSqlClientTests
{
    private const string SchemaContext = "analytics.vw_Cpi(ReferenceDate, IndexValue)";

    private static readonly IReadOnlyDictionary<string, object?> NoParameters =
        new Dictionary<string, object?>();

    /// <summary>No coverage line, for tests that are not about explaining an empty result.</summary>
    private const string NoCoverage = "";

    /// <summary>
    /// The model every test asks for unless it is about the choice itself, spelled short so the
    /// call sites still read as the call rather than as the enum.
    /// </summary>
    private const AssistantModelChoice Cloud = AssistantModelChoice.Cloud;
    private const AssistantModelChoice Local = AssistantModelChoice.Local;

    // ------------------------------------------------------------ response parsing

    [Fact]
    public async Task ReadsTheSqlOutOfAWellFormedResponse()
    {
        var client = ClientReturning(Completion("{\"sql\": \"SELECT 1 FROM analytics.vw_Cpi\"}"));

        var result = await client.GenerateSqlAsync("How high is CPI?", SchemaContext, [], Cloud, default);

        Assert.Equal("SELECT 1 FROM analytics.vw_Cpi", result.Sql);
        Assert.Equal("deepseek-chat", result.ModelName);
    }

    [Fact]
    public async Task ReadsTheSqlEvenWhenTheModelFencesItsJson()
    {
        // Models wrap JSON in ```json fences often enough that treating it as a parse failure
        // would reject perfectly good queries.
        var client = ClientReturning(Completion(
            "```json\n{\"sql\": \"SELECT 1 FROM analytics.vw_Cpi\"}\n```"));

        var result = await client.GenerateSqlAsync("How high is CPI?", SchemaContext, [], Cloud, default);

        Assert.Equal("SELECT 1 FROM analytics.vw_Cpi", result.Sql);
    }

    [Theory]
    // The model reporting it cannot answer is a legitimate outcome, not a failure of the call.
    [InlineData("{\"sql\": null}")]
    [InlineData("{\"sql\": \"null\"}")]
    [InlineData("{\"sql\": \"\"}")]
    [InlineData("{\"sql\": \"   \"}")]
    // Shapes that are not the contract at all.
    [InlineData("{\"query\": \"SELECT 1\"}")]
    [InlineData("I'm sorry, I can't answer that from this data.")]
    [InlineData("{ this is not json")]
    [InlineData("")]
    public async Task ReportsNoSqlRatherThanThrowingOnOutputItCannotUse(string content)
    {
        var client = ClientReturning(Completion(content));

        var result = await client.GenerateSqlAsync("Unanswerable", SchemaContext, [], Cloud, default);

        Assert.Null(result.Sql);
    }

    // ------------------------------------------------- parameters and explanation

    [Fact]
    public async Task ReadsParametersAndTheExplanationAlongsideTheSql()
    {
        var client = ClientReturning(Completion("""
            {"sql": "SELECT IndexValue FROM analytics.vw_Cpi WHERE ReferenceDate = @month",
             "parameters": {"@month": "2025-06-01"},
             "explanation": "Reads the monthly CPI index value for one month."}
            """));

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Cloud, default);

        Assert.Equal("2025-06-01", result.Parameters["@month"]);
        Assert.Equal("Reads the monthly CPI index value for one month.", result.Explanation);
    }

    [Fact]
    public async Task NormalisesParameterNamesToALeadingAt()
    {
        var client = ClientReturning(Completion("""
            {"sql": "SELECT 1 FROM analytics.vw_Cpi WHERE ReferenceDate = @month",
             "parameters": {"month": "2025-06-01"}}
            """));

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Cloud, default);

        Assert.True(result.Parameters.ContainsKey("@month"));
    }

    // Bound as the type they arrive as, so a comparison against a numeric column is not a string
    // comparison that happens to work. Written as separate facts rather than a Theory because
    // xUnit narrows a boxed 42L in InlineData back to int, which makes the long case untestable
    // through that route.

    [Fact]
    public async Task BindsAWholeNumberAsAnInteger() =>
        Assert.Equal(42L, await ScalarParameterAsync("42"));

    [Fact]
    public async Task BindsAFractionalNumberAsADouble() =>
        Assert.Equal(4.25d, await ScalarParameterAsync("4.25"));

    [Fact]
    public async Task BindsABooleanAsABoolean() =>
        Assert.Equal(true, await ScalarParameterAsync("true"));

    [Fact]
    public async Task BindsAStringAsAString() =>
        Assert.Equal("text", await ScalarParameterAsync("\"text\""));

    [Theory]
    // A parameter is a single value. Serialising a structure back to text would bind something
    // the model did not mean and a reviewer would not expect.
    [InlineData("[1,2,3]")]
    [InlineData("{\"nested\": 1}")]
    [InlineData("null")]
    public async Task RefusesToBindAnythingThatIsNotAScalar(string json)
    {
        Assert.Null(await ScalarParameterAsync(json));
    }

    /// <summary>Round-trips one JSON value through the client and returns what it bound.</summary>
    private static async Task<object?> ScalarParameterAsync(string json)
    {
        var client = ClientReturning(Completion(
            $$$"""{"sql": "SELECT 1 FROM analytics.vw_Cpi WHERE X = @v", "parameters": {"@v": {{{json}}} }}"""));

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Cloud, default);

        return result.Parameters["@v"];
    }

    [Fact]
    public async Task TreatsAMissingParameterBagAsNoParameters()
    {
        var client = ClientReturning(Completion("""{"sql": "SELECT 1 FROM analytics.vw_Cpi"}"""));

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Cloud, default);

        Assert.Empty(result.Parameters);
    }

    [Fact]
    public async Task AsksForParametersAndAnExplanationInThePrompt()
    {
        var handler = new CapturingHandler(Completion("""{"sql": null}"""));
        var client = ClientOver(handler);

        await client.GenerateSqlAsync("q", SchemaContext, [], Cloud, default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var system = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;

        Assert.Contains("parameters", system);
        Assert.Contains("explanation", system);
        Assert.Contains("refusal", system);
    }

    // --------------------------------------------------------- refusal classification

    [Theory]
    [InlineData("not_a_data_question", NlRefusalKind.NotADataQuestion)]
    [InlineData("unanswerable", NlRefusalKind.Unanswerable)]
    // "What is SOFR?" is neither chatter nor unanswerable, and answering it with a greeting reads
    // as the assistant having misheard. The model only classifies — the text is fixed on the
    // service side, so this path still cannot produce a figure.
    [InlineData("about_cpi", NlRefusalKind.AboutCpi)]
    [InlineData("about_sofr", NlRefusalKind.AboutSofr)]
    [InlineData("about_platform", NlRefusalKind.AboutPlatform)]
    public async Task ClassifiesWhyItRefused(string refusal, NlRefusalKind expected)
    {
        var client = ClientReturning(Completion($$"""{"sql": null, "refusal": "{{refusal}}"}"""));

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Cloud, default);

        Assert.Null(result.Sql);
        Assert.Equal(expected, result.Refusal);
    }

    [Theory]
    // Anything unrecognised stays Unanswerable. A rejected query in the review queue that turns
    // out to be chatter costs a reviewer a glance; chatter filed as nothing costs them the probe
    // hiding behind it.
    //
    // Every case here still carries an "sql" key, which is what makes it a refusal at all: the
    // model answered in the shape it was asked for and declined within it. Only the reason is
    // missing or unrecognised, and that is what defaults.
    [InlineData("""{"sql": null}""")]
    [InlineData("""{"sql": null, "refusal": "something else"}""")]
    [InlineData("""{"sql": null, "refusal": null}""")]
    // No "sql" key at all, but a reason given for its absence. A stated refusal is a stated
    // refusal — the model reached a judgement and said so, and holding the shape against it would
    // file a real answer as a malfunction.
    [InlineData("""{"refusal": "unanswerable"}""")]
    public async Task DefaultsToUnanswerableWhenTheRefusalIsNotOneItKnows(string content)
    {
        var client = ClientReturning(Completion(content));

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Cloud, default);

        Assert.Equal(NlRefusalKind.Unanswerable, result.Refusal);
    }

    [Theory]
    // A reply that never got as far as the requested shape. Kept apart from Unanswerable because
    // the two point at different faults: Unanswerable says the views cannot serve the question,
    // and a reviewer seeing a run of them goes looking for a missing view. These say the model did
    // not answer in the shape it was asked for — nothing is wrong with the schema, and looking
    // there is looking in the one place the answer is not.
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{\"explanation\": \"I had a think about it\"}")]  // neither an "sql" key nor a reason
    [InlineData("{\"sql\": \"SELECT 1 FROM analytics.vw_Cpi")]     // cut off mid-statement
    public async Task ReportsAResponseItCouldNotReadAsUnreadable(string content)
    {
        var client = ClientReturning(Completion(content));

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Cloud, default);

        Assert.Null(result.Sql);
        Assert.Equal(NlRefusalKind.Unreadable, result.Refusal);
    }

    [Fact]
    public async Task ReportsNoRefusalWhenItReturnedSql()
    {
        var client = ClientReturning(Completion("""{"sql": "SELECT 1 FROM analytics.vw_Cpi"}"""));

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Cloud, default);

        Assert.Equal(NlRefusalKind.None, result.Refusal);
    }

    [Fact]
    public async Task CarriesTokenUsageBackForTheAuditLog()
    {
        var client = ClientReturning(Completion(
            "{\"sql\": \"SELECT 1 FROM analytics.vw_Cpi\"}", promptTokens: 412, completionTokens: 17));

        var result = await client.GenerateSqlAsync("How high is CPI?", SchemaContext, [], Cloud, default);

        Assert.Equal(412, result.PromptTokens);
        Assert.Equal(17, result.CompletionTokens);
    }

    // -------------------------------------------------------------- prompt building

    [Fact]
    public async Task SendsTheSchemaContextAsTheSystemMessageAndTheQuestionAsTheUserMessage()
    {
        var handler = new CapturingHandler(Completion("{\"sql\": null}"));
        var client = ClientOver(handler);

        await client.GenerateSqlAsync("What was CPI in June?", SchemaContext, [], Cloud, default);

        var body = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");

        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains(SchemaContext, messages[0].GetProperty("content").GetString());

        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("What was CPI in June?", messages[1].GetProperty("content").GetString());
    }

    // ------------------------------------------------------- conversation history

    [Fact]
    public async Task ReplaysEarlierTurnsBetweenTheSystemMessageAndTheQuestion()
    {
        var handler = new CapturingHandler(Completion("{\"sql\": null}"));
        var client = ClientOver(handler);

        ConversationTurn[] history =
        [
            new("what is the average cpi of 2022",
                "SELECT AnnualValue FROM analytics.vw_CpiAnnual WHERE ReferenceYear = @year",
                new Dictionary<string, object?> { ["@year"] = 2022L })
        ];

        await client.GenerateSqlAsync(
            "and what is the average cpi of the year before that", SchemaContext, history, Cloud, default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var messages = doc.RootElement.GetProperty("messages");

        // system, then the earlier exchange as a real user/assistant pair, then the new question.
        // Order is the whole point: a follow-up resolved against turns placed after it would be
        // resolved against nothing.
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());

        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("what is the average cpi of 2022", messages[1].GetProperty("content").GetString());

        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());

        Assert.Equal("user", messages[3].GetProperty("role").GetString());
        Assert.Equal("and what is the average cpi of the year before that",
            messages[3].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ReplaysTheEarlierStatementWithItsParametersBound()
    {
        var handler = new CapturingHandler(Completion("{\"sql\": null}"));
        var client = ClientOver(handler);

        ConversationTurn[] history =
        [
            new("what is the average cpi of 2022",
                "SELECT AnnualValue FROM analytics.vw_CpiAnnual WHERE ReferenceYear = @year",
                new Dictionary<string, object?> { ["@year"] = 2022L })
        ];

        await client.GenerateSqlAsync("and the year before that", SchemaContext, history, Cloud, default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var replayed = doc.RootElement.GetProperty("messages")[2].GetProperty("content").GetString()!;

        using var turn = JsonDocument.Parse(replayed);

        Assert.Equal(
            "SELECT AnnualValue FROM analytics.vw_CpiAnnual WHERE ReferenceYear = @year",
            turn.RootElement.GetProperty("sql").GetString());

        // The referent lives in the parameters, not the statement. Replaying the SQL with @year
        // still unbound would leave "the year before that" pointing at a placeholder.
        Assert.Equal(2022,
            turn.RootElement.GetProperty("parameters").GetProperty("@year").GetInt32());
    }

    [Fact]
    public async Task SendsNoExtraMessagesWhenThereIsNoHistory()
    {
        var handler = new CapturingHandler(Completion("{\"sql\": null}"));
        var client = ClientOver(handler);

        await client.GenerateSqlAsync("What was CPI in June?", SchemaContext, [], Cloud, default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);

        Assert.Equal(2, doc.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task NeverReplaysTheAnswersThemselves()
    {
        // The one thing history must not carry. A model shown "CPI in 2022 was 292.655" can
        // satisfy a follow-up without querying anything, and a recalled figure is indistinguishable
        // in the UI from a collected one. ConversationTurn has no field for it; this pins that the
        // client never invents one.
        var handler = new CapturingHandler(Completion("{\"sql\": null}"));
        var client = ClientOver(handler);

        ConversationTurn[] history =
        [
            new("what is the average cpi of 2022",
                "SELECT AnnualValue FROM analytics.vw_CpiAnnual WHERE ReferenceYear = @year",
                new Dictionary<string, object?> { ["@year"] = 2022L })
        ];

        await client.GenerateSqlAsync("and the year before that", SchemaContext, history, Cloud, default);

        Assert.DoesNotContain("292.655", handler.LastRequestBody!);
    }

    [Fact]
    public async Task AsksForDeterministicJsonWhenGeneratingSql()
    {
        // Temperature 0 and JSON mode are what make the SQL step reproducible enough to review.
        var handler = new CapturingHandler(Completion("{\"sql\": null}"));
        var client = ClientOver(handler);

        await client.GenerateSqlAsync("What was CPI in June?", SchemaContext, [], Cloud, default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);

        Assert.Equal(0, doc.RootElement.GetProperty("temperature").GetDouble());
        Assert.Equal("json_object",
            doc.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task DoesNotAskForJsonWhenSummarising()
    {
        // The answer is prose for a human; forcing JSON mode here would garble it.
        var handler = new CapturingHandler(Completion("CPI stood at 320.3 in June."));
        var client = ClientOver(handler);

        await client.SummariseResultsAsync("q", "SELECT 1", NoParameters, "[]", NoCoverage, Cloud, default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);

        Assert.Equal(JsonValueKind.Null,
            doc.RootElement.GetProperty("response_format").ValueKind);
    }

    [Fact]
    public async Task SendsTheQuestionSqlAndResultsToTheSummariser()
    {
        var handler = new CapturingHandler(Completion("CPI stood at 320.3 in June."));
        var client = ClientOver(handler);

        await client.SummariseResultsAsync(
            "What was CPI in June?", "SELECT IndexValue FROM analytics.vw_Cpi", NoParameters,
            "[{\"IndexValue\":320.3}]", NoCoverage, Cloud, default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var user = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;

        Assert.Contains("What was CPI in June?", user);
        Assert.Contains("SELECT IndexValue FROM analytics.vw_Cpi", user);
        Assert.Contains("320.3", user);
    }

    [Fact]
    public async Task SendsTheCoverageWindowToTheSummariser()
    {
        // Without it an empty result is a dead end: "no rows" and "that period is before the
        // series begins" are the same JSON, and only the coverage tells them apart.
        var handler = new CapturingHandler(Completion("SOFR begins in April 2018."));
        var client = ClientOver(handler);

        await client.SummariseResultsAsync(
            "give sofr of may 2001",
            "SELECT RatePercent FROM analytics.vw_Sofr WHERE EffectiveDate >= @from",
            new Dictionary<string, object?> { ["@from"] = "2001-05-01" },
            "[]",
            """
            What this platform holds:
            - SOFR (analytics.vw_Sofr, EffectiveDate): 2018-04-02 to 2026-08-10
            """,
            Cloud,
            default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var user = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;

        Assert.Contains("2018-04-02", user);
    }

    [Fact]
    public async Task SendsTheBoundParametersToTheSummariserToo()
    {
        // A follow-up's question text does not say what it is about — "the year before that" names
        // no year. This step never sees the conversation, so the bound parameter is the only place
        // the subject of the answer is written down. Without it the summariser reads the pronoun
        // literally and reports the right figures as the wrong ones.
        var handler = new CapturingHandler(Completion("CPI averaged 270.97 in 2021."));
        var client = ClientOver(handler);

        await client.SummariseResultsAsync(
            "and what is the average cpi of the year before that",
            "SELECT AnnualValue FROM analytics.vw_CpiAnnual WHERE ReferenceYear = @year",
            new Dictionary<string, object?> { ["@year"] = 2021L },
            "[{\"AnnualValue\":270.97}]",
            NoCoverage,
            Cloud,
            default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var user = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;

        Assert.Contains("@year", user);
        Assert.Contains("2021", user);
    }

    [Fact]
    public async Task ReturnsTheSummaryTextTrimmed()
    {
        var client = ClientReturning(Completion("  CPI stood at 320.3 in June.\n"));

        var result = await client.SummariseResultsAsync("q", "SELECT 1", NoParameters, "[]", NoCoverage, Cloud, default);

        Assert.Equal("CPI stood at 320.3 in June.", result.AnswerText);
    }

    [Fact]
    public async Task SendsTheApiKeyAsABearerToken()
    {
        var handler = new CapturingHandler(Completion("{\"sql\": null}"));
        var client = ClientOver(handler, apiKey: "sk-test-key");

        await client.GenerateSqlAsync("q", SchemaContext, [], Cloud, default);

        Assert.Equal("Bearer", handler.LastAuthorizationScheme);
        Assert.Equal("sk-test-key", handler.LastAuthorizationParameter);
    }

    // ------------------------------------------------------------- configuration

    [Fact]
    public async Task RefusesToCallTheApiWithoutAKeyAndSaysWhichSettingIsMissing()
    {
        var client = ClientOver(new CapturingHandler(Completion("{\"sql\": null}")), apiKey: "");

        var ex = await Assert.ThrowsAsync<AssistantNotConfiguredException>(
            () => client.GenerateSqlAsync("q", SchemaContext, [], Cloud, default));

        Assert.Contains("Assistant:ApiKey", ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    public async Task ReportsAGatewayRejectionAsAConfigurationFault(HttpStatusCode status)
    {
        // Usually a key issued by a different provider than BaseUrl points at. Left to
        // EnsureSuccessStatusCode it becomes an opaque 500 that says nothing about the cause.
        var client = ClientOver(new CapturingHandler("{}") { Status = status });

        var ex = await Assert.ThrowsAsync<AssistantNotConfiguredException>(
            () => client.GenerateSqlAsync("q", SchemaContext, [], Cloud, default));

        Assert.Contains("Assistant:ApiKey", ex.Message);
    }

    // ------------------------------------------------------------- choosing a model

    [Theory]
    // The whole of what the choice does on the wire: a different address, a different model id,
    // and — because only Ollama's own API accepts a context size — a different dialect entirely.
    // Everything above this line is asserted once, for Cloud, because it is the same code path.
    [InlineData(Cloud, "https://api.deepseek.test/chat/completions", "deepseek-chat")]
    [InlineData(Local, "http://localhost:11434/api/chat", "qwen3.5:2b")]
    public async Task SendsTheQuestionToTheChosenProvider(
        AssistantModelChoice choice, string expectedUrl, string expectedModel)
    {
        var handler = new CapturingHandler(Completion("""{"sql": null}"""));
        var client = ClientOver(handler);

        await client.GenerateSqlAsync("q", SchemaContext, [], choice, default);

        Assert.Equal(expectedUrl, handler.LastRequestUri?.ToString());

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal(expectedModel, doc.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task SummarisesWithTheSameProviderThatWroteTheStatement()
    {
        // Both halves of a turn go to one model, or the model name recorded against the turn is
        // true of half of it.
        var handler = new CapturingHandler(OllamaCompletion("CPI stood at 320.3 in June."));
        var client = ClientOver(handler);

        await client.SummariseResultsAsync("q", "SELECT 1", NoParameters, "[]", NoCoverage, Local, default);

        Assert.Equal("http://localhost:11434/api/chat", handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task ReadsAReplyInOllamaSNativeShape()
    {
        // The native API answers with one `message`, not a list of `choices`. Read with the OpenAI
        // reader it yields nothing at all, which would arrive as an unreadable response — a model
        // blamed for a shape this client asked for.
        var client = ClientReturning(OllamaCompletion(
            """{"sql": "SELECT 1 FROM analytics.vw_Cpi"}""",
            promptTokens: 3891, completionTokens: 213));

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        Assert.Equal("SELECT 1 FROM analytics.vw_Cpi", result.Sql);
        Assert.Equal(3891, result.PromptTokens);
        Assert.Equal(213, result.CompletionTokens);
    }

    [Fact]
    public async Task ReportsTheModelThatActuallyAnsweredRatherThanTheOneAskedFor()
    {
        // What goes in the audit record is the id the gateway was given, not the enum the caller
        // chose. The two are recorded separately for exactly this reason: point Local at a
        // different model tomorrow and today's turns still say what served them.
        var client = ClientReturning(OllamaCompletion("""{"sql": "SELECT 1 FROM analytics.vw_Cpi"}"""));

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        Assert.Equal("qwen3.5:2b", result.ModelName);
    }

    // --------------------------------------------------------------- the context window

    [Fact]
    public async Task AsksForAContextWindowLargeEnoughToAnswerIn()
    {
        // The setting the whole local path turns on. Ollama loads a model at 4096 tokens by
        // default and the schema prompt alone is around 3,900, which leaves roughly 200 for the
        // reply — so every statement longer than a trivial one stopped mid-JSON and was recorded
        // as an unreadable response after a multi-minute wait.
        var handler = new CapturingHandler(OllamaCompletion("""{"sql": null}"""));
        var client = ClientOver(handler);

        await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);

        Assert.Equal(16384,
            doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task AsksForOneWholeReplyRatherThanAStreamOfThem()
    {
        // Ollama streams by default, which would hand back a sequence of JSON objects where this
        // client reads exactly one — and the first of them carries an empty message.
        var handler = new CapturingHandler(OllamaCompletion("""{"sql": null}"""));
        var client = ClientOver(handler);

        await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);

        Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task AsksTheLocalModelForJsonWhenWritingSqlAndProseWhenSummarising()
    {
        // The native spelling of response_format. Generation needs the object shape or there is
        // nothing to parse; the summary is prose for a human, and constraining that to JSON would
        // garble it.
        var writing = new CapturingHandler(OllamaCompletion("""{"sql": null}"""));
        var summarising = new CapturingHandler(OllamaCompletion("CPI stood at 320.3 in June."));

        await ClientOver(writing).GenerateSqlAsync("q", SchemaContext, [], Local, default);
        await ClientOver(summarising).SummariseResultsAsync(
            "q", "SELECT 1", NoParameters, "[]", NoCoverage, Local, default);

        using var whenWriting = JsonDocument.Parse(writing.LastRequestBody!);
        using var whenSummarising = JsonDocument.Parse(summarising.LastRequestBody!);

        Assert.Equal("json", whenWriting.RootElement.GetProperty("format").GetString());
        Assert.False(whenSummarising.RootElement.TryGetProperty("format", out _));
    }

    [Fact]
    public async Task LeavesTheContextToTheServerWhenNoneIsConfigured()
    {
        // Absent, not zero: num_ctx 0 is not "the default", and a server reading it literally
        // would load the model with no room at all.
        var handler = new CapturingHandler(OllamaCompletion("""{"sql": null}"""));
        var client = ClientOver(handler, localContextTokens: null);

        await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);

        Assert.False(doc.RootElement.GetProperty("options").TryGetProperty("num_ctx", out _));
    }

    [Fact]
    public async Task StillSpeaksOpenAiToALocalServerConfiguredThatWay()
    {
        // Another server behind the local option — llama.cpp, LM Studio, vLLM — speaks
        // /chat/completions and knows nothing of Ollama's shape. Its context then has to be set on
        // the server, which is exactly the limitation the native default exists to escape.
        var handler = new CapturingHandler(Completion("""{"sql": "SELECT 1 FROM analytics.vw_Cpi"}"""));
        var client = ClientOver(handler, localApi: "OpenAi", localBaseUrl: "http://localhost:8080/v1/");

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        Assert.Equal("http://localhost:8080/v1/chat/completions", handler.LastRequestUri?.ToString());
        Assert.Equal("SELECT 1 FROM analytics.vw_Cpi", result.Sql);
    }

    [Fact]
    public async Task AsksTheLocalModelWithoutRequiringAKey()
    {
        // Ollama authenticates nothing. Demanding Assistant:ApiKey before a request that never
        // leaves the machine would make the local option depend on the very thing it exists to
        // avoid needing.
        var handler = new CapturingHandler(Completion("""{"sql": null}"""));
        var client = ClientOver(handler, apiKey: "");

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        Assert.Null(result.Sql);
        Assert.Equal("ollama", handler.LastAuthorizationParameter);
    }

    [Fact]
    public async Task SendsNoAuthorizationHeaderWhenTheLocalKeyIsDeliberatelyEmpty()
    {
        // An operator who empties this means it. Sent blank instead, some servers answer 400 for a
        // header that should simply not have been there.
        var handler = new CapturingHandler(Completion("""{"sql": null}"""));
        var client = ClientOver(handler, localApiKey: "");

        await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        Assert.Null(handler.LastAuthorizationScheme);
    }

    [Fact]
    public async Task ReportsAModelTheLocalServerDoesNotHaveAsSomethingToPull()
    {
        // 404 from a local server means the model was never downloaded, which has a one-line fix.
        // As a raw EnsureSuccessStatusCode failure it is a 500 that says "Not Found" and nothing
        // about which name was not found or how to get it.
        var client = ClientOver(new CapturingHandler("{}") { Status = HttpStatusCode.NotFound });

        var ex = await Assert.ThrowsAsync<AssistantNotConfiguredException>(
            () => client.GenerateSqlAsync("q", SchemaContext, [], Local, default));

        Assert.Contains("qwen3.5:2b", ex.Message);
        Assert.Contains("ollama pull", ex.Message);
    }

    [Fact]
    public async Task ReportsAnUnreachableLocalServerAsOneToStart()
    {
        // The ordinary state of a machine where Ollama simply is not running, and the first thing
        // anyone choosing this option will hit.
        var client = ClientOver(new ThrowingHandler(new HttpRequestException("Connection refused")));

        var ex = await Assert.ThrowsAsync<AssistantNotConfiguredException>(
            () => client.GenerateSqlAsync("q", SchemaContext, [], Local, default));

        Assert.Contains("localhost:11434", ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task LeavesAnUnreachableCloudGatewayAsATransportFailure()
    {
        // Deliberately not dressed up as a configuration fault. A hosted gateway that cannot be
        // reached is a network problem or an outage — nothing the reader can fix by editing a
        // setting — and the advice attached to the local case would be wrong here.
        var client = ClientOver(new ThrowingHandler(new HttpRequestException("No such host")));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GenerateSqlAsync("q", SchemaContext, [], Cloud, default));
    }

    [Theory]
    // Ollama's native API is at the root and its OpenAI-compatible one under /v1/. This setting
    // used to have to be the latter, so a configuration carried over from then would otherwise ask
    // for /v1/api/chat and get a 404 saying nothing about why. Trailing slash optional throughout:
    // Uri resolves a relative path against the last segment of its base, which silently turns
    // ".../v1" + "api/chat" into a URL that exists nowhere and reads like the one that was meant.
    [InlineData("http://localhost:11434/")]
    [InlineData("http://localhost:11434")]
    [InlineData("http://localhost:11434/v1/")]
    [InlineData("http://localhost:11434/v1")]
    public async Task ReachesOllamaFromAnyReasonableSpellingOfItsAddress(string baseUrl)
    {
        var handler = new CapturingHandler(OllamaCompletion("""{"sql": null}"""));
        var client = ClientOver(handler, localBaseUrl: baseUrl);

        await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        Assert.Equal("http://localhost:11434/api/chat", handler.LastRequestUri?.ToString());
    }

    [Fact]
    public async Task TurnsOffThinkingOnTheLocalModel()
    {
        // Not a tuning preference — the option does not work without this. qwen3.5:2b reasons by
        // default, Ollama returns that reasoning in a separate field while content stays empty, and
        // a 2B model on a CPU spends the whole token budget deliberating and stops on length with
        // nothing in content. The turn is then filed as RejectedUnreadableResponse, minutes later.
        var handler = new CapturingHandler(OllamaCompletion("""{"sql": null}"""));
        var client = ClientOver(handler);

        await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);

        Assert.False(doc.RootElement.GetProperty("think").GetBoolean());
    }

    [Fact]
    public async Task TurnsOffThinkingTheOtherWayWhenSpeakingOpenAiToALocalServer()
    {
        // The same instruction, spelled the way the other endpoint understands it. Neither name
        // works on the other API, so the dialect decides which is sent.
        var handler = new CapturingHandler(Completion("""{"sql": null}"""));
        var client = ClientOver(handler, localApi: "OpenAi", localBaseUrl: "http://localhost:8080/v1/");

        await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);

        Assert.Equal("none", doc.RootElement.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task SendsNoReasoningEffortToTheHostedGateway()
    {
        // Absent, not null. The hosted gateway is not a reasoning model and has no use for the
        // field, and an OpenAI-shaped API is within its rights to reject a parameter it does not
        // know rather than ignore it.
        var handler = new CapturingHandler(Completion("""{"sql": null}"""));
        var client = ClientOver(handler);

        await client.GenerateSqlAsync("q", SchemaContext, [], Cloud, default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);

        Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task OmitsReasoningEffortWhenTheLocalServerIsConfiguredWithout()
    {
        // A local server whose model does not reason has no use for it, and emptying the setting
        // has to mean "do not send this", not "send an empty string" — a value no gateway defines.
        var handler = new CapturingHandler(Completion("""{"sql": null}"""));
        var client = ClientOver(
            handler, localReasoningEffort: "", localApi: "OpenAi",
            localBaseUrl: "http://localhost:8080/v1/");

        await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);

        Assert.False(doc.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task SetsNoTokenCeilingOnTheLocalModel()
    {
        // num_predict absent, not null and not a large number: nothing here is metered, and a cap
        // only buys a way to fail. Hitting one truncates the JSON wrapping the SQL rather than the
        // SQL, so the reply arrives unparseable after the full wait.
        var handler = new CapturingHandler(OllamaCompletion("""{"sql": null}"""));
        var client = ClientOver(handler);

        await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);

        Assert.False(doc.RootElement.GetProperty("options").TryGetProperty("num_predict", out _));
    }

    [Fact]
    public async Task KeepsTheTokenCeilingOnTheHostedGateway()
    {
        // The setting exists to bound what a metered API is asked to produce, and that reason still
        // holds for the gateway that bills per token.
        var handler = new CapturingHandler(Completion("""{"sql": null}"""));
        var client = ClientOver(handler);

        await client.GenerateSqlAsync("q", SchemaContext, [], Cloud, default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);

        Assert.Equal(3000, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task WaitsIndefinitelyForTheLocalModelByDefault()
    {
        // The regression this exists for: a 120-second ceiling here produced a TaskCanceledException
        // out of HttpClient.SendAsync, which the API reported as a bare 500 — the whole wait spent
        // and nothing to show for it. A model slower than any deadline anyone would pick must be
        // waited for, not cut off.
        var handler = new SlowHandler(
            TimeSpan.FromMilliseconds(250), OllamaCompletion("""{"sql": null}"""));
        var client = ClientOver(handler);

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        Assert.Equal(NlRefusalKind.Unanswerable, result.Refusal);
    }

    [Fact]
    public async Task StillStopsWaitingForTheLocalModelWhenTheCallerGivesUp()
    {
        // No deadline is not the same as no way out. The request's own token is still linked, so a
        // browser that navigates away takes the model call with it — which is what makes an
        // unbounded wait acceptable rather than merely unbounded.
        var handler = new SlowHandler(TimeSpan.FromSeconds(30), Completion("""{"sql": null}"""));
        var client = ClientOver(handler);

        using var caller = new CancellationTokenSource();
        var asking = client.GenerateSqlAsync("q", SchemaContext, [], Local, caller.Token);

        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
    }

    [Fact]
    public async Task StillBoundsTheHostedGateway()
    {
        // Removing the local deadline must not quietly remove the gateway's. A remote call that
        // hangs is a network fault worth abandoning, not a slow machine worth waiting for.
        var handler = new SlowHandler(TimeSpan.FromSeconds(30), Completion("""{"sql": null}"""));
        var client = ClientOver(handler, cloudTimeoutSeconds: 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GenerateSqlAsync("q", SchemaContext, [], Cloud, default));
    }

    // ------------------------------------------------------- a reply that was cut off

    [Fact]
    public async Task ReportsACutOffReplyAsTruncatedRatherThanUnreadable()
    {
        // The failure a small model behind a modest context window actually produces, and the one
        // that cost the most to diagnose: the schema prompt runs to several thousand tokens, what
        // is left is what the answer has to fit in, and a statement longer than that stops
        // mid-string. It never parses — but "could not be read" sends the reader after a model that
        // ignores its output format, which is the one thing not wrong here.
        var client = ClientReturning(OllamaCompletion(
            """{"sql": "SELECT ReferenceDate, IndexValue FROM analytics.vw_Cpi WHERE Ref""",
            doneReason: "length"));

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        Assert.Null(result.Sql);
        Assert.Equal(NlRefusalKind.Truncated, result.Refusal);
    }

    [Fact]
    public async Task ReportsACutOffReplyFromTheHostedGatewayToo()
    {
        // The same reading through the other dialect, where the field is finish_reason rather than
        // done_reason. A cap can be hit on either side; only the spelling differs.
        var client = ClientReturning(Completion(
            """{"sql": "SELECT ReferenceDate, IndexValue FROM analytics.vw_Cpi WHERE Ref""",
            finishReason: "length"));

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Cloud, default);

        Assert.Equal(NlRefusalKind.Truncated, result.Refusal);
    }

    [Fact]
    public async Task KeepsTheTruncatedReadingEvenWhenThePartialReplyHappensToParse()
    {
        // A reply can stop on a token boundary that leaves valid JSON behind — cut after the "sql"
        // key's value but before "explanation", say. The provider still said it ran out of room,
        // and that is a fact about the call rather than an opinion about the text: what came back
        // is a fragment of an answer, not an answer.
        var client = ClientReturning(OllamaCompletion("""{"parameters": {}}""", doneReason: "length"));

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        Assert.Equal(NlRefusalKind.Truncated, result.Refusal);
    }

    [Fact]
    public async Task DoesNotCallACompleteReplyTruncated()
    {
        // finish_reason "stop" is the model having said all it meant to. A reply that is unreadable
        // for any other reason must keep being reported as unreadable, or the distinction buys
        // nothing.
        var client = ClientReturning(OllamaCompletion("I had a think about it", doneReason: "stop"));

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        Assert.Equal(NlRefusalKind.Unreadable, result.Refusal);
    }

    [Fact]
    public async Task StillReadsASoundReplyThatMerelyStoppedOnLength()
    {
        // Being cut off is only a verdict when nothing usable survives. A complete statement that
        // happened to end exactly at the limit is still a statement, and refusing it would throw
        // away a good query over where the reply stopped.
        var client = ClientReturning(OllamaCompletion(
            """{"sql": "SELECT 1 FROM analytics.vw_Cpi"}""", doneReason: "length"));

        var result = await client.GenerateSqlAsync("q", SchemaContext, [], Local, default);

        Assert.Equal("SELECT 1 FROM analytics.vw_Cpi", result.Sql);
        Assert.Equal(NlRefusalKind.None, result.Refusal);
    }

    [Fact]
    public async Task PutsTheSameQuestionToBothModels()
    {
        // The prompts do not vary by provider, and it matters that they do not: a comparison
        // between the two only means something if they were asked the same thing.
        var cloud = new CapturingHandler(Completion("""{"sql": null}"""));
        var local = new CapturingHandler(Completion("""{"sql": null}"""));

        await ClientOver(cloud).GenerateSqlAsync("What was CPI in June?", SchemaContext, [], Cloud, default);
        await ClientOver(local).GenerateSqlAsync("What was CPI in June?", SchemaContext, [], Local, default);

        static string Messages(CapturingHandler handler) =>
            JsonDocument.Parse(handler.LastRequestBody!).RootElement
                .GetProperty("messages").GetRawText();

        Assert.Equal(Messages(cloud), Messages(local));
    }

    // --------------------------------------------------------------------- helpers

    /// <param name="finishReason">
    /// Why the model stopped: <c>"stop"</c> for a complete reply, <c>"length"</c> for one cut off
    /// having run out of room.
    /// </param>
    /// <summary>
    /// A reply in Ollama's native shape, which is what the local provider speaks by default.
    /// </summary>
    /// <remarks>
    /// Close enough to <see cref="Completion"/> to look like the same thing and different in every
    /// field this client reads: one <c>message</c> rather than a list of <c>choices</c>,
    /// <c>done_reason</c> rather than <c>finish_reason</c>, and token counts named for the work
    /// done rather than for what was billed.
    /// </remarks>
    private static string OllamaCompletion(
        string content,
        int promptTokens = 0,
        int completionTokens = 0,
        string doneReason = "stop") =>
        JsonSerializer.Serialize(new
        {
            message = new { role = "assistant", content },
            done = true,
            done_reason = doneReason,
            prompt_eval_count = promptTokens,
            eval_count = completionTokens
        });

    private static string Completion(
        string content,
        int promptTokens = 0,
        int completionTokens = 0,
        string finishReason = "stop") =>
        JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new { message = new { role = "assistant", content }, finish_reason = finishReason }
            },
            usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens }
        });

    private static ChatCompletionsNlToSqlClient ClientReturning(string responseJson) =>
        ClientOver(new CapturingHandler(responseJson));

    private static ChatCompletionsNlToSqlClient ClientOver(
        HttpMessageHandler handler,
        string apiKey = "sk-test",
        string localApiKey = "ollama",
        string localBaseUrl = "http://localhost:11434/",
        string localReasoningEffort = "none",
        int cloudTimeoutSeconds = 30,
        string localApi = "Ollama",
        int? localContextTokens = 16384)
    {
        var options = Options.Create(new AssistantOptions
        {
            ApiKey = apiKey,
            Model = "deepseek-chat",
            BaseUrl = "https://api.deepseek.test/",
            RequestTimeoutSeconds = cloudTimeoutSeconds,
            Local = new LocalModelOptions
            {
                ApiKey = localApiKey,
                Model = "qwen3.5:2b",
                BaseUrl = localBaseUrl,
                ReasoningEffort = localReasoningEffort,
                Api = localApi,
                ContextTokens = localContextTokens
            }
        });

        // No BaseAddress. The endpoint is a property of the chosen provider now, so a client that
        // had one would hide a bug where the per-request URL was left relative.
        return new ChatCompletionsNlToSqlClient(new HttpClient(handler), options);
    }

    /// <summary>Stands in for the provider, recording what the client actually sent.</summary>
    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            LastRequestUri = request.RequestUri;
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;

            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>A transport that fails the way an absent server does: before any response exists.</summary>
    private sealed class ThrowingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(failure);
    }

    /// <summary>
    /// A transport that takes its time, and honours cancellation while it does.
    /// </summary>
    /// <remarks>
    /// Stands in for a small model on a slow machine. The delay is awaited on the token it was
    /// given rather than slept through, because these tests are about which token is linked to the
    /// call — a handler that ignored cancellation would pass whether or not the client wired one up.
    /// </remarks>
    private sealed class SlowHandler(TimeSpan delay, string responseJson) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
