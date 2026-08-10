using System.Net;
using System.Text;
using System.Text.Json;
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
/// </remarks>
public class DeepSeekNlToSqlClientTests
{
    private const string SchemaContext = "analytics.vw_Cpi(ReferenceDate, IndexValue)";

    // ------------------------------------------------------------ response parsing

    [Fact]
    public async Task ReadsTheSqlOutOfAWellFormedResponse()
    {
        var client = ClientReturning(Completion("{\"sql\": \"SELECT 1 FROM analytics.vw_Cpi\"}"));

        var result = await client.GenerateSqlAsync("How high is CPI?", SchemaContext, default);

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

        var result = await client.GenerateSqlAsync("How high is CPI?", SchemaContext, default);

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

        var result = await client.GenerateSqlAsync("Unanswerable", SchemaContext, default);

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

        var result = await client.GenerateSqlAsync("q", SchemaContext, default);

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

        var result = await client.GenerateSqlAsync("q", SchemaContext, default);

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

        var result = await client.GenerateSqlAsync("q", SchemaContext, default);

        return result.Parameters["@v"];
    }

    [Fact]
    public async Task TreatsAMissingParameterBagAsNoParameters()
    {
        var client = ClientReturning(Completion("""{"sql": "SELECT 1 FROM analytics.vw_Cpi"}"""));

        var result = await client.GenerateSqlAsync("q", SchemaContext, default);

        Assert.Empty(result.Parameters);
    }

    [Fact]
    public async Task AsksForParametersAndAnExplanationInThePrompt()
    {
        var handler = new CapturingHandler(Completion("""{"sql": null}"""));
        var client = ClientOver(handler);

        await client.GenerateSqlAsync("q", SchemaContext, default);

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
    public async Task ClassifiesWhyItRefused(string refusal, NlRefusalKind expected)
    {
        var client = ClientReturning(Completion($$"""{"sql": null, "refusal": "{{refusal}}"}"""));

        var result = await client.GenerateSqlAsync("q", SchemaContext, default);

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

        var result = await client.GenerateSqlAsync("q", SchemaContext, default);

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

        var result = await client.GenerateSqlAsync("q", SchemaContext, default);

        Assert.Null(result.Sql);
        Assert.Equal(NlRefusalKind.Unreadable, result.Refusal);
    }

    [Fact]
    public async Task ReportsNoRefusalWhenItReturnedSql()
    {
        var client = ClientReturning(Completion("""{"sql": "SELECT 1 FROM analytics.vw_Cpi"}"""));

        var result = await client.GenerateSqlAsync("q", SchemaContext, default);

        Assert.Equal(NlRefusalKind.None, result.Refusal);
    }

    [Fact]
    public async Task CarriesTokenUsageBackForTheAuditLog()
    {
        var client = ClientReturning(Completion(
            "{\"sql\": \"SELECT 1 FROM analytics.vw_Cpi\"}", promptTokens: 412, completionTokens: 17));

        var result = await client.GenerateSqlAsync("How high is CPI?", SchemaContext, default);

        Assert.Equal(412, result.PromptTokens);
        Assert.Equal(17, result.CompletionTokens);
    }

    // -------------------------------------------------------------- prompt building

    [Fact]
    public async Task SendsTheSchemaContextAsTheSystemMessageAndTheQuestionAsTheUserMessage()
    {
        var handler = new CapturingHandler(Completion("{\"sql\": null}"));
        var client = ClientOver(handler);

        await client.GenerateSqlAsync("What was CPI in June?", SchemaContext, default);

        var body = handler.LastRequestBody!;
        using var doc = JsonDocument.Parse(body);
        var messages = doc.RootElement.GetProperty("messages");

        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains(SchemaContext, messages[0].GetProperty("content").GetString());

        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("What was CPI in June?", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task AsksForDeterministicJsonWhenGeneratingSql()
    {
        // Temperature 0 and JSON mode are what make the SQL step reproducible enough to review.
        var handler = new CapturingHandler(Completion("{\"sql\": null}"));
        var client = ClientOver(handler);

        await client.GenerateSqlAsync("What was CPI in June?", SchemaContext, default);

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

        await client.SummariseResultsAsync("q", "SELECT 1", "[]", default);

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
            "What was CPI in June?", "SELECT IndexValue FROM analytics.vw_Cpi", "[{\"IndexValue\":320.3}]", default);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var user = doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;

        Assert.Contains("What was CPI in June?", user);
        Assert.Contains("SELECT IndexValue FROM analytics.vw_Cpi", user);
        Assert.Contains("320.3", user);
    }

    [Fact]
    public async Task ReturnsTheSummaryTextTrimmed()
    {
        var client = ClientReturning(Completion("  CPI stood at 320.3 in June.\n"));

        var result = await client.SummariseResultsAsync("q", "SELECT 1", "[]", default);

        Assert.Equal("CPI stood at 320.3 in June.", result.AnswerText);
    }

    [Fact]
    public async Task SendsTheApiKeyAsABearerToken()
    {
        var handler = new CapturingHandler(Completion("{\"sql\": null}"));
        var client = ClientOver(handler, apiKey: "sk-test-key");

        await client.GenerateSqlAsync("q", SchemaContext, default);

        Assert.Equal("Bearer", handler.LastAuthorizationScheme);
        Assert.Equal("sk-test-key", handler.LastAuthorizationParameter);
    }

    // ------------------------------------------------------------- configuration

    [Fact]
    public async Task RefusesToCallTheApiWithoutAKeyAndSaysWhichSettingIsMissing()
    {
        var client = ClientOver(new CapturingHandler(Completion("{\"sql\": null}")), apiKey: "");

        var ex = await Assert.ThrowsAsync<AssistantNotConfiguredException>(
            () => client.GenerateSqlAsync("q", SchemaContext, default));

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
            () => client.GenerateSqlAsync("q", SchemaContext, default));

        Assert.Contains("Assistant:ApiKey", ex.Message);
    }

    // --------------------------------------------------------------------- helpers

    private static string Completion(string content, int promptTokens = 0, int completionTokens = 0) =>
        JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content } } },
            usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens }
        });

    private static DeepSeekNlToSqlClient ClientReturning(string responseJson) =>
        ClientOver(new CapturingHandler(responseJson));

    private static DeepSeekNlToSqlClient ClientOver(CapturingHandler handler, string apiKey = "sk-test")
    {
        var options = Options.Create(new AssistantOptions
        {
            ApiKey = apiKey,
            Model = "deepseek-chat",
            BaseUrl = "https://api.deepseek.test/"
        });

        return new DeepSeekNlToSqlClient(new HttpClient(handler), options);
    }

    /// <summary>Stands in for the provider, recording what the client actually sent.</summary>
    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }
        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;

            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
