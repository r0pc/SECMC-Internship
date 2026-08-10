// backend/src/DataIntelligence.Infrastructure/Ai/DeepSeekNlToSqlClient.cs
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataIntelligence.Core.Exceptions;
using DataIntelligence.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace DataIntelligence.Infrastructure.Ai;

/// <summary>
/// Calls a chat-completions API in OpenAI's request shape to turn a question into SQL, and a
/// result set into prose. Configured against DeepSeek's own API, model <c>deepseek-v4-flash</c>.
/// </summary>
/// <remarks>
/// The wire format is OpenAI's <c>/chat/completions</c> rather than Anthropic's <c>/messages</c> —
/// a system message is just another entry in the messages array, and JSON-only output is requested
/// via <c>response_format</c> rather than by prompt instruction alone. Any gateway speaking that
/// shape is a <c>BaseUrl</c> and <c>Model</c> change, not a code change; a provider that does not
/// means implementing <see cref="INlToSqlClient"/> once more. Nothing else in the assistant
/// pipeline depends on either.
/// </remarks>
public sealed class DeepSeekNlToSqlClient : INlToSqlClient
{
    private readonly HttpClient _httpClient;
    private readonly AssistantOptions _options;

    public DeepSeekNlToSqlClient(HttpClient httpClient, IOptions<AssistantOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        _httpClient.BaseAddress ??= new Uri(_options.BaseUrl);
    }

    public async Task<NlToSqlResult> GenerateSqlAsync(
        string question,
        string schemaContext,
        IReadOnlyList<ConversationTurn> history,
        CancellationToken cancellationToken)
    {
        var system = schemaContext
            + """


            Respond with JSON only, no prose, in this exact shape:
            {"sql": "<statement or null>", "parameters": {"@name": <value>}, "explanation": "<one or two sentences>", "refusal": null}

            When "sql" is null, set "refusal" to "not_a_data_question" if the input was a greeting,
            thanks, or anything not asking about data, and to "unanswerable" if it was a genuine
            data question these views cannot answer. Leave "refusal" null whenever you return SQL.

            Put every literal the question supplies into "parameters" and reference it from the
            statement by name — write `WHERE ReferenceDate = @month`, not `WHERE ReferenceDate =
            '2025-06-01'`. Column and table names are part of the statement and are never
            parameters. Use an empty object when the query needs none.

            "explanation" says in plain language what the statement does and which view it reads.
            It is shown to the person who asked and stored for review, so describe the query rather
            than restating the question.
            """;

        var stopwatch = Stopwatch.StartNew();

        var response = await SendAsync(
            system, question, jsonMode: true, ReplayHistory(history), cancellationToken);
        var json = ExtractJson(response.Text);

        string? sql = null;
        string? explanation = null;

        // Starts Unreadable and is only moved off it once the response proves to be the shape that
        // was asked for. Prose where JSON was requested, or an object with no "sql" key at all, is
        // then reported as what it is rather than being filed as the model's considered judgement
        // that the schema cannot answer the question — two facts that send a reviewer to opposite
        // ends of the system.
        var refusal = NlRefusalKind.Unreadable;
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // ExtractJson only ever hands back a slice starting at '{', so in practice this holds
            // and nothing below is reached with an array or a bare scalar. It is asserted here
            // anyway because the cost of being wrong is not a refusal: TryGetProperty throws
            // InvalidOperationException on a non-object, which is not the JsonException caught
            // below, so it would escape as a 500 rather than as an answer.
            if (root.ValueKind != JsonValueKind.Object)
            {
                return NlToSqlResult.NoSql(
                    NlRefusalKind.Unreadable, _options.Model, response.PromptTokens,
                    response.CompletionTokens, (int)stopwatch.ElapsedMilliseconds);
            }

            if (root.TryGetProperty("sql", out var sqlElement))
            {
                // The key being present is what says the model answered in the requested shape;
                // whether it holds a statement or null is the model's answer *within* that shape.
                // So a deliberate {"sql": null} that forgot to classify itself is a refusal with a
                // missing reason — not an unreadable response — and defaults as it always did.
                refusal = NlRefusalKind.Unanswerable;

                if (sqlElement.ValueKind == JsonValueKind.String)
                {
                    sql = sqlElement.GetString();
                }
            }

            if (root.TryGetProperty("refusal", out var why2) && why2.ValueKind == JsonValueKind.String)
            {
                // Anything unrecognised stays Unanswerable: a rejected query in the review queue
                // that turns out to be chatter costs a reviewer a glance, where chatter filed as
                // nothing at all costs them the probe hiding behind it.
                refusal = why2.GetString() switch
                {
                    "not_a_data_question" => NlRefusalKind.NotADataQuestion,
                    _ => NlRefusalKind.Unanswerable
                };
            }

            if (root.TryGetProperty("explanation", out var why) && why.ValueKind == JsonValueKind.String)
            {
                explanation = why.GetString();
            }

            if (root.TryGetProperty("parameters", out var bag) && bag.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in bag.EnumerateObject())
                {
                    var name = property.Name.StartsWith('@') ? property.Name : "@" + property.Name;
                    parameters[name] = ReadScalar(property.Value);
                }
            }
        }
        catch (JsonException)
        {
            // Malformed model output becomes a refusal downstream, not a crash. Both fields are
            // reset rather than left as the partial parse found them: a document that threw
            // halfway may have yielded a refusal string before it failed, and reporting that as
            // the model's answer would describe a broken response as a deliberate one.
            sql = null;
            refusal = NlRefusalKind.Unreadable;
        }

        if (string.IsNullOrWhiteSpace(sql) || sql == "null")
        {
            return NlToSqlResult.NoSql(
                refusal, _options.Model, response.PromptTokens, response.CompletionTokens,
                (int)stopwatch.ElapsedMilliseconds);
        }

        return new NlToSqlResult(
            sql, parameters, explanation,
            _options.Model, response.PromptTokens, response.CompletionTokens,
            (int)stopwatch.ElapsedMilliseconds);
    }

    public async Task<NlSummaryResult> SummariseResultsAsync(
        string question,
        string generatedSql,
        IReadOnlyDictionary<string, object?> parameters,
        string resultsJson,
        CancellationToken cancellationToken)
    {
        // The last two sentences are about follow-ups. This step is given one question at a time
        // and never sees the conversation, so a question like "and the year before that?" arrives
        // with its referent missing — and a model trying to honour the wording will report the
        // figures it was handed as the wrong ones, or apologise for not having a year that was
        // never asked for. The generation step already resolved it; the parameters carry the
        // resolution, and they are what the answer is actually about.
        const string system =
            "You answer questions about US economic data (CPI, SOFR) from a JSON result set. "
            + "Be concise, cite the actual figures, and never invent a number that is not in the "
            + "data given to you. If the result set is empty, say so plainly. "
            + "The question may be a follow-up whose wording refers to something you cannot see "
            + "(\"that year\", \"the year before that\", \"the same for SOFR\"); the SQL and its "
            + "parameters are that reference already resolved. Where the wording and the query "
            + "disagree about what was asked for, the query is right — answer for what was "
            + "queried, and never say data is missing merely because the wording implies a period "
            + "the query did not ask for.";

        var user = $"Question: {question}\n\nSQL used: {generatedSql}\n\n"
            + $"Parameters bound to it (JSON): {JsonSerializer.Serialize(parameters)}\n\n"
            + $"Results (JSON): {resultsJson}";

        var stopwatch = Stopwatch.StartNew();
        var response = await SendAsync(system, user, jsonMode: false, [], cancellationToken);

        return new NlSummaryResult(response.Text.Trim(), response.CompletionTokens, (int)stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Turns earlier exchanges into the alternating user/assistant messages a chat model already
    /// understands.
    /// </summary>
    /// <remarks>
    /// Replayed as real conversation turns rather than summarised into the system prompt, because
    /// the assistant half is then literally the JSON the model produced last time. Every prior turn
    /// therefore doubles as a worked example in exactly the format being demanded — context and
    /// few-shot in one — where a prose digest ("earlier they asked about 2022") would supply the
    /// context and quietly drop the demonstration.
    /// <para>
    /// The parameters are replayed with the statement, and they carry the weight: "the year before
    /// that" is answerable only because <c>@year: 2022</c> is sitting in the turn above, and a
    /// statement shown with its placeholders still unbound would resolve to nothing.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<DeepSeekMessage> ReplayHistory(IReadOnlyList<ConversationTurn> history)
    {
        if (history.Count == 0)
        {
            return [];
        }

        var messages = new List<DeepSeekMessage>(history.Count * 2);

        foreach (var turn in history)
        {
            messages.Add(new DeepSeekMessage("user", turn.Question));
            messages.Add(new DeepSeekMessage("assistant", JsonSerializer.Serialize(new
            {
                sql = turn.Sql,
                parameters = turn.Parameters
            })));
        }

        return messages;
    }

    private async Task<(string Text, int? PromptTokens, int? CompletionTokens)> SendAsync(
        string system,
        string userMessage,
        bool jsonMode,
        IReadOnlyList<DeepSeekMessage> priorTurns,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new AssistantNotConfiguredException(
                $"'{AssistantOptions.SectionName}:ApiKey' is not configured, so the assistant cannot "
                + "reach its model. Set it with: dotnet user-secrets set "
                + $"\"{AssistantOptions.SectionName}:ApiKey\" \"<key>\" --project src\\DataIntelligence.Api");
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var requestBody = new DeepSeekRequest(
            _options.Model,
            [
                new DeepSeekMessage("system", system),
                .. priorTurns,
                new DeepSeekMessage("user", userMessage)
            ],
            0,
            _options.MaxOutputTokens,
            jsonMode ? new DeepSeekResponseFormat("json_object") : null);

        // The key goes on the request rather than on DefaultRequestHeaders: the typed client is
        // pooled and reused, and mutating shared default headers from a per-call path is a race.
        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var response = await _httpClient.SendAsync(request, linked.Token);

        // A key the gateway rejects is a configuration fault, not a transient one — report it as
        // such rather than letting EnsureSuccessStatusCode turn it into an opaque 500. The most
        // common cause is a key issued by a different provider than BaseUrl points at.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired
            or HttpStatusCode.Forbidden)
        {
            throw new AssistantNotConfiguredException(
                $"The model gateway at '{_httpClient.BaseAddress}' rejected the request with "
                + $"{(int)response.StatusCode} {response.StatusCode}. Check that "
                + $"'{AssistantOptions.SectionName}:ApiKey' is a key for that gateway and that it "
                + "has credit for the configured model.");
        }

        response.EnsureSuccessStatusCode();

        var parsed = await response.Content.ReadFromJsonAsync<DeepSeekResponse>(linked.Token)
            ?? throw new InvalidOperationException("Empty response from the DeepSeek API.");

        var text = parsed.Choices.FirstOrDefault()?.Message.Content ?? string.Empty;

        return (text, parsed.Usage?.PromptTokens, parsed.Usage?.CompletionTokens);
    }

    /// <summary>
    /// Converts one JSON value into something <c>SqlCommand</c> can bind.
    /// </summary>
    /// <remarks>
    /// Arrays and nested objects are refused — mapped to null — rather than serialised back to
    /// text. A parameter is a single value by definition, and quietly turning a structure into a
    /// string would bind something the model did not mean and the reviewer would not expect.
    /// </remarks>
    private static object? ReadScalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        // Boxed to object on both arms deliberately. Left as `whole : value.GetDouble()` the
        // ternary takes double as its common type, and every whole number binds as a float —
        // which SQL Server then has to convert on every row of a comparison against an int column.
        JsonValueKind.Number => value.TryGetInt64(out var whole) ? (object)whole : value.GetDouble(),
        _ => null
    };

    /// <summary>
    /// Even in JSON mode the model occasionally wraps the object in a fenced code block; this
    /// pulls the object out regardless.
    /// </summary>
    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : "{}";
    }

    private sealed record DeepSeekRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<DeepSeekMessage> Messages,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("response_format")] DeepSeekResponseFormat? ResponseFormat);

    private sealed record DeepSeekMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record DeepSeekResponseFormat(
        [property: JsonPropertyName("type")] string Type);

    private sealed record DeepSeekResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<DeepSeekChoice> Choices,
        [property: JsonPropertyName("usage")] DeepSeekUsage? Usage);

    private sealed record DeepSeekChoice(
        [property: JsonPropertyName("message")] DeepSeekMessage Message);

    private sealed record DeepSeekUsage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens);
}