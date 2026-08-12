// backend/src/DataIntelligence.Infrastructure/Ai/ChatCompletionsNlToSqlClient.cs
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Exceptions;
using DataIntelligence.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataIntelligence.Infrastructure.Ai;

/// <summary>
/// Calls a chat-completions API in OpenAI's request shape to turn a question into SQL, and a
/// result set into prose. Two are configured: a hosted gateway (DeepSeek's own API, model
/// <c>deepseek-v4-flash</c>) and a model served on this machine (Ollama, <c>qwen3.5:2b</c>), chosen
/// per question by <see cref="AssistantModelChoice"/>.
/// </summary>
/// <remarks>
/// Named for the wire format rather than for either provider, because the format is the whole
/// reason one class can serve both. The request is OpenAI's <c>/chat/completions</c> rather than
/// Anthropic's <c>/messages</c> — a system message is just another entry in the messages array,
/// and JSON-only output is requested via <c>response_format</c> rather than by prompt instruction
/// alone. Ollama exposes exactly that shape under <c>/v1/</c>, so running a model locally is a
/// second <c>BaseUrl</c> and <c>Model</c> here and not a second implementation. Any other gateway
/// speaking it is the same change; one that does not means implementing
/// <see cref="INlToSqlClient"/> once more, and nothing else in the assistant pipeline depends on
/// either.
/// <para>
/// The two providers differ in more than their address, and the differences are all handled at the
/// edges rather than in the prompt: a local server authenticates nothing, takes far longer to
/// answer, and reports a model it has not downloaded as a 404 where a hosted gateway would never
/// see one. The prompts themselves are identical, which is the point — the same question put to
/// both is the only way the choice between them means anything.
/// </para>
/// </remarks>
public sealed class ChatCompletionsNlToSqlClient : INlToSqlClient
{
    private readonly HttpClient _httpClient;
    private readonly AssistantOptions _options;
    private readonly ILogger<ChatCompletionsNlToSqlClient> _logger;

    public ChatCompletionsNlToSqlClient(
        HttpClient httpClient,
        IOptions<AssistantOptions> options,
        ILogger<ChatCompletionsNlToSqlClient>? logger = null)
    {
        _httpClient = httpClient;
        _options = options.Value;

        // Optional so the tests can construct this with two arguments as they always have. A client
        // that logged nothing unless someone remembered to pass a logger would be the wrong default
        // for the one class in this pipeline whose failures are otherwise invisible.
        _logger = logger ?? NullLogger<ChatCompletionsNlToSqlClient>.Instance;
    }

    public async Task<NlToSqlResult> GenerateSqlAsync(
        string question,
        string schemaContext,
        IReadOnlyList<ConversationTurn> history,
        AssistantModelChoice model,
        CancellationToken cancellationToken)
    {
        var provider = Resolve(model);

        // Output contract first, then the schema — which puts every stable byte of the prompt at
        // the front and the only part that moves, today's date and the coverage window, at the very
        // end of it.
        //
        // That ordering is worth more than it looks. Both providers reuse a prompt by its prefix and
        // stop at the first byte that differs: a hosted gateway bills a cached prefix at a fraction
        // of the input rate, and a local server can skip re-reading a prefix whose KV cache it still
        // holds — which on a 2B model on a CPU is most of the wait, since reading the prompt is the
        // bulk of the work. With the date sitting in the middle, everything after it was re-read on
        // every question and re-billed every day. The tail after it is now about 200 tokens.
        //
        // Nothing here depends on the order for its meaning: the shape block refers to nothing
        // above it, the views are still stated before the semantics that say "not listed above", and
        // the worked examples still resolve their dates against a "today" that follows them.
        var system = """
            Respond with JSON only, no prose, in this exact shape:
            {"sql": "<statement or null>", "parameters": {"@name": <value>}, "explanation": "<one or two sentences>", "refusal": null}

            When "sql" is null, set "refusal" to one of:
              "not_a_data_question" — a greeting, thanks, or anything not asking about data.
              "about_cpi", "about_sofr" — asks what CPI or SOFR *is*: how it is defined, who
                                 publishes it, what it measures, what the index base means. Not what
                                 it *was*: "what was CPI in June" wants a figure and needs SQL.
              "about_platform" — what this assistant or platform is, what data it holds, what it can
                                 answer, how often it collects.
              "unanswerable"   — a genuine data question these views cannot answer.
            Leave "refusal" null whenever you return SQL. The "about_" values are classifications,
            not requests to write the answer — the reply is fixed text on the other side, so put no
            definition in "explanation" and state no figure.

            """ + schemaContext;

        var stopwatch = Stopwatch.StartNew();

        var response = await SendAsync(
            provider, system, question, jsonMode: true, ReplayHistory(history), cancellationToken);
        var json = ExtractJson(response.Text);

        string? sql = null;
        string? explanation = null;

        // Starts Unreadable and is only moved off it once the response proves to be the shape that
        // was asked for. Prose where JSON was requested, or an object with no "sql" key at all, is
        // then reported as what it is rather than being filed as the model's considered judgement
        // that the schema cannot answer the question — two facts that send a reviewer to opposite
        // ends of the system.
        //
        // This carries more weight now that a small local model is one of the choices. A 2B model
        // ignores an output format far more often than a hosted one does, and a run of these
        // against Local is the expected shape of that — not evidence of anything wrong with the
        // views. The recorded model name is what tells the two readings apart.
        // A reply the provider itself reported as cut off starts as Truncated rather than
        // Unreadable. Both mean nothing usable came back, and they send a reader to opposite
        // places: one to a model that will not follow an output format, the other to a context
        // window that left no room to answer in.
        var refusal = response.Truncated ? NlRefusalKind.Truncated : NlRefusalKind.Unreadable;
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
                    refusal, provider.Model, response.PromptTokens,
                    response.CompletionTokens, (int)stopwatch.ElapsedMilliseconds,
                    response.CachedPromptTokens);
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
                    "about_cpi" => NlRefusalKind.AboutCpi,
                    "about_sofr" => NlRefusalKind.AboutSofr,
                    "about_platform" => NlRefusalKind.AboutPlatform,
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
        catch (JsonException ex)
        {
            // Malformed model output becomes a refusal downstream, not a crash. Both fields are
            // reset rather than left as the partial parse found them: a document that threw
            // halfway may have yielded a refusal string before it failed, and reporting that as
            // the model's answer would describe a broken response as a deliberate one.
            //
            // A reply the provider already told us was cut off keeps that reading. It will always
            // fail to parse — that is what being cut off means — and overwriting the reason with
            // "unreadable" here would throw away the one fact that explains it.
            sql = null;

            if (refusal != NlRefusalKind.Truncated)
            {
                refusal = NlRefusalKind.Unreadable;
            }

            _logger.LogDebug(ex, "The model's reply did not parse as JSON.");
        }

        if (string.IsNullOrWhiteSpace(sql) || sql == "null")
        {
            // The reply itself, which is otherwise kept nowhere. The transcript records what the
            // model was understood to have said, and these are exactly the turns where it was
            // understood as nothing — so without this line, diagnosing a run of them means
            // reconstructing the prompt by hand and hoping the failure repeats.
            //
            // Only for the two outcomes that mean nothing usable came back. A model that refused
            // deliberately said so in the shape it was asked for, and its reply is already on the
            // record.
            if (refusal is NlRefusalKind.Unreadable or NlRefusalKind.Truncated)
            {
                _logger.LogWarning(
                    "Nothing usable came back from the {Choice} model '{Model}' at {Endpoint} "
                    + "({Reason}, {PromptTokens} prompt + {CompletionTokens} completion tokens). "
                    + "Raw reply: {Reply}",
                    provider.Choice, provider.Model, provider.Endpoint, refusal,
                    response.PromptTokens, response.CompletionTokens, Excerpt(response.Text));
            }

            return NlToSqlResult.NoSql(
                refusal, provider.Model, response.PromptTokens, response.CompletionTokens,
                (int)stopwatch.ElapsedMilliseconds, response.CachedPromptTokens);
        }

        return new NlToSqlResult(
            sql, parameters, explanation,
            provider.Model, response.PromptTokens, response.CompletionTokens,
            (int)stopwatch.ElapsedMilliseconds, NlRefusalKind.None, response.CachedPromptTokens);
    }

    public async Task<NlSummaryResult> SummariseResultsAsync(
        string question,
        string generatedSql,
        IReadOnlyDictionary<string, object?> parameters,
        string resultsJson,
        string coverage,
        AssistantModelChoice model,
        CancellationToken cancellationToken)
    {
        // The prose rule is second because it is about the whole answer rather than any part of it.
        // A model left to choose its own format reaches for a markdown table whenever it has more
        // than two figures to give, and this answer is rendered as text — so the reader gets pipes
        // and dashes where a sentence was meant. Naming the constructs is what makes it hold; "plain
        // prose" alone is read as advice about tone.
        //
        // The middle sentences are about follow-ups. This step is given one question at a time and
        // never sees the conversation, so a question like "and the year before that?" arrives with
        // its referent missing — and a model trying to honour the wording will report the figures it
        // was handed as the wrong ones, or apologise for not having a year that was never asked for.
        // The generation step already resolved it; the parameters carry the resolution, and they are
        // what the answer is actually about.
        //
        // The line about "columns" and "rows" is what makes the compact result shape readable — see
        // ResultSetFormatter. Without it a columnar payload is a puzzle rather than a saving.
        const string system =
            "You answer questions about US economic data (CPI, SOFR) from a query result. "
            + "Be concise, cite the actual figures, and never invent a number that is not in the "
            + "data given to you. Write plain prose — sentences and paragraphs, with the figures "
            + "inside them. No tables, no bullet or numbered lists, no headings, no code fences, no "
            + "bold or italic markers. "
            // The two sentences that described the result's shape, and the one saying a long
            // result carries a note, are gone: the JSON shows its own shape, and that note is
            // written into the payload by ResultSetFormatter and says how to read itself.
            + "A follow-up's wording may refer to something you cannot see (\"that year\", \"the "
            + "year before that\", \"the same for SOFR\"); the SQL and its parameters are that "
            + "reference already resolved, so answer for what was queried rather than for the "
            + "wording. That holds only while the question names no period of its own. "
            // Said once rather than three times. What went was the restatement, not the rule:
            // period-mismatch still says whose fault it is and still forbids reporting the named
            // period as unavailable, which is the pair that fixed the June-2025 answer.
            + "If the question names a period and the query asked for a different one, the query "
            + "is wrong: say the question asked for the period it named, that the query asked for "
            + "another, and that this is a fault on our side worth asking again. Never tell the "
            + "reader a period is unavailable when the query did not ask for it. "
            + "Otherwise, when there are no rows, say why and not only that there are none: if the "
            + "period queried falls outside the coverage below, say so and give the range that is "
            + "held. If it is covered and still empty, say that plainly and invent no coverage "
            + "explanation.";

        var user = $"Question: {question}\n\nSQL used: {generatedSql}\n\n"
            + $"Parameters bound to it (JSON): {JsonSerializer.Serialize(parameters)}\n\n"
            + $"{coverage}\n"
            + $"Results: {resultsJson}";

        var stopwatch = Stopwatch.StartNew();
        var response = await SendAsync(
            Resolve(model), system, user, jsonMode: false, [], cancellationToken);

        return new NlSummaryResult(
            response.Text.Trim(), response.CompletionTokens, (int)stopwatch.ElapsedMilliseconds,
            response.PromptTokens, response.CachedPromptTokens);
    }

    /// <summary>
    /// Turns earlier exchanges into messages: the most recent few as the alternating user/assistant
    /// pairs a chat model already understands, and anything older as one summary line each.
    /// </summary>
    /// <remarks>
    /// The recent turns are replayed whole rather than described, because the assistant half is then
    /// literally the JSON the model produced last time. Each one doubles as a worked example in
    /// exactly the format being demanded — context and few-shot in one — where a prose digest
    /// ("earlier they asked about 2022") would supply the context and quietly drop the
    /// demonstration. The parameters are replayed with the statement, and they carry the weight:
    /// "the year before that" is answerable only because <c>@year: 2022</c> is sitting in the turn
    /// above, and a statement shown with its placeholders still unbound would resolve to nothing.
    /// <para>
    /// Older turns are summarised instead, because the demonstration stops paying for itself long
    /// before the reference does. What a follow-up ever reaches back for is the subject and the
    /// period — which view, which year — and those are a few tokens each, where the statements they
    /// came from are hundreds: a nested LAG query costs as much to replay as a third of the schema.
    /// Two turns of few-shot is already more than any model needs to learn a JSON shape it was also
    /// handed a specification for, and the sixth-oldest statement teaches it nothing the newest one
    /// does not. See <see cref="AssistantOptions.VerbatimHistoryTurns"/>.
    /// </para>
    /// <para>
    /// Note that history is replayed to whichever model is answering now, including one that did
    /// not write it. That is deliberate: a conversation is the user's, not the model's, and
    /// switching models mid-chat must not silently drop the referent a follow-up depends on. The
    /// worked example a strong model left behind is, if anything, worth more to a weaker one.
    /// </para>
    /// </remarks>
    private IReadOnlyList<ChatMessage> ReplayHistory(IReadOnlyList<ConversationTurn> history)
    {
        if (history.Count == 0)
        {
            return [];
        }

        var verbatim = Math.Clamp(_options.VerbatimHistoryTurns, 0, history.Count);
        var summarised = history.Count - verbatim;

        var messages = new List<ChatMessage>(verbatim * 2 + 1);

        if (summarised > 0)
        {
            messages.Add(new ChatMessage("system", Digest(history, summarised)));
        }

        for (var i = summarised; i < history.Count; i++)
        {
            var turn = history[i];

            messages.Add(new ChatMessage("user", turn.Question));
            messages.Add(new ChatMessage("assistant", JsonSerializer.Serialize(new
            {
                sql = Collapse(turn.Sql),
                parameters = turn.Parameters
            })));
        }

        return messages;
    }

    /// <summary>
    /// The oldest <paramref name="count"/> turns as one line each: what was asked, which views
    /// answered it, and what the statement was bound to.
    /// </summary>
    /// <remarks>
    /// Deliberately not the statement. A reference like "that year" or "the same for SOFR" resolves
    /// against the subject and the period, and both survive here — the view names say which series,
    /// the bound values say which period. What is dropped is the SELECT list, the joins and the
    /// window functions, which are the expensive part of a statement and the part no follow-up ever
    /// points at.
    /// <para>
    /// It stays subject to the same rule as the verbatim replay, and for the same reason: no figure
    /// from any result set appears here, only what was asked and what was bound. A model that can
    /// see last turn's answer can satisfy this turn's follow-up without querying anything.
    /// </para>
    /// </remarks>
    private static string Digest(IReadOnlyList<ConversationTurn> history, int count)
    {
        var builder = new StringBuilder(
            "Earlier turns of this conversation, oldest first, summarised — the question, the "
            + "view(s) that answered it, and the values it was bound to. Use them to resolve a "
            + "reference; the statements themselves are not repeated.\n");

        for (var i = 0; i < count; i++)
        {
            var turn = history[i];

            builder.Append("- \"").Append(turn.Question).Append('"');

            var views = ViewsIn(turn.Sql);

            if (views.Length > 0)
            {
                builder.Append(" → ").Append(string.Join(", ", views));
            }

            if (turn.Parameters.Count > 0)
            {
                builder.Append(" [")
                    .Append(string.Join(", ", turn.Parameters.Select(p => $"{p.Key}={p.Value}")))
                    .Append(']');
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>Every distinct <c>analytics.*</c> object a statement names, in the order it names them.</summary>
    private static string[] ViewsIn(string sql) =>
        AnalyticsObject.Matches(sql)
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static readonly Regex AnalyticsObject =
        new(@"analytics\.\w+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// A statement with its formatting whitespace collapsed to single spaces.
    /// </summary>
    /// <remarks>
    /// Only for the replayed copy — the stored statement keeps whatever the model wrote, because
    /// that is what the audit record is for. A model that indents its SQL over eight lines pays for
    /// that indentation again on every subsequent question of the session, and the line breaks
    /// carry nothing the model needs to resolve a reference against.
    /// </remarks>
    private static string Collapse(string sql) => Whitespace.Replace(sql, " ").Trim();

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private async Task<(string Text, int? PromptTokens, int? CompletionTokens, bool Truncated,
        int? CachedPromptTokens)> SendAsync(
        Provider provider,
        string system,
        string userMessage,
        bool jsonMode,
        IReadOnlyList<ChatMessage> priorTurns,
        CancellationToken cancellationToken)
    {
        if (provider.RequiresApiKey && string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            throw new AssistantNotConfiguredException(
                $"'{AssistantOptions.SectionName}:ApiKey' is not configured, so the assistant cannot "
                + "reach its model. Set it with: dotnet user-secrets set "
                + $"\"{AssistantOptions.SectionName}:ApiKey\" \"<key>\" --project src\\DataIntelligence.Api"
                + " — or ask the local model instead, which needs no key.");
        }

        // No deadline at all when the provider has none — see LocalModelOptions.RequestTimeoutSeconds.
        // The caller's token is still linked in either case, so an unlimited call is still a
        // cancellable one: a browser that goes away takes the model call with it.
        using var timeoutCts = provider.Timeout is { } limit
            ? new CancellationTokenSource(limit)
            : null;

        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        ChatMessage[] messages =
        [
            new ChatMessage("system", system),
            .. priorTurns,
            new ChatMessage("user", userMessage)
        ];

        object requestBody = provider.Dialect switch
        {
            Wire.Ollama => new OllamaRequest(
                provider.Model,
                messages,

                // Single response, not a token stream. This client waits for a whole reply and
                // parses it; Ollama streams by default and would otherwise hand back a series of
                // JSON objects where one was expected.
                Stream: false,

                Format: jsonMode ? "json" : null,

                // The native spelling of "do not think". Ollama's OpenAI surface calls the same
                // thing reasoning_effort, and neither name works on the other endpoint.
                Think: false,

                Options: new OllamaOptions(
                    NumCtx: provider.ContextTokens,
                    Temperature: 0,
                    NumPredict: provider.MaxOutputTokens)),

            _ => new ChatRequest(
                provider.Model,
                messages,
                0,
                provider.MaxOutputTokens,
                jsonMode ? new ChatResponseFormat("json_object") : null,
                provider.ReasoningEffort)
        };

        // The endpoint is absolute rather than relative to the client's BaseAddress, because the
        // address is now a property of the chosen provider rather than of the client: the typed
        // client is pooled and shared across requests, and two questions choosing different models
        // at the same moment would otherwise be reading and writing one BaseAddress. The key goes
        // on the request for the same reason — mutating shared DefaultRequestHeaders from a
        // per-call path is a race.
        using var request = new HttpRequestMessage(HttpMethod.Post, provider.Endpoint)
        {
            Content = JsonContent.Create(requestBody)
        };

        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        }

        HttpResponseMessage response;

        try
        {
            response = await _httpClient.SendAsync(request, linked.Token);
        }
        catch (HttpRequestException ex) when (provider.Choice == AssistantModelChoice.Local)
        {
            // Nothing listening on a loopback port is the ordinary state of a machine where the
            // model server simply is not running, and it is the first thing anyone choosing this
            // option will hit. Left as a raw transport failure it surfaces as a 500 that names a
            // socket; said plainly it names the one command that fixes it.
            throw new AssistantNotConfiguredException(
                $"Nothing answered at '{provider.Endpoint}' ({ex.Message}). The local model needs a "
                + "server running on this machine — start Ollama and check it is reachable, or ask "
                + "the cloud model instead. Configured under "
                + $"'{AssistantOptions.SectionName}:Local:BaseUrl'.",
                ex);
        }

        using (response)
        {
            // A key the gateway rejects is a configuration fault, not a transient one — report it as
            // such rather than letting EnsureSuccessStatusCode turn it into an opaque 500. The most
            // common cause is a key issued by a different provider than BaseUrl points at.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired
                or HttpStatusCode.Forbidden)
            {
                throw new AssistantNotConfiguredException(
                    $"The model gateway at '{provider.Endpoint}' rejected the request with "
                    + $"{(int)response.StatusCode} {response.StatusCode}. Check that "
                    + $"'{provider.ApiKeySetting}' is a key for that gateway and that it "
                    + "has credit for the configured model.");
            }

            // A local server answers 404 for a model it does not have, which is a different fault
            // to a wrong address and has a different fix. Only for Local: a hosted gateway 404 is
            // more likely a wrong BaseUrl, and guessing at "pull the model" would send the reader
            // to a command that does not apply.
            if (response.StatusCode == HttpStatusCode.NotFound
                && provider.Choice == AssistantModelChoice.Local)
            {
                throw new AssistantNotConfiguredException(
                    $"The local server at '{provider.Endpoint}' has no model named "
                    + $"'{provider.Model}'. Download it first — for Ollama that is "
                    + $"`ollama pull {provider.Model}` — or point "
                    + $"'{AssistantOptions.SectionName}:Local:Model' at one it already has "
                    + "(`ollama list`). Model names include the tag, so 'qwen3.5' and 'qwen3.5:2b' "
                    + "are not the same request.");
            }

            response.EnsureSuccessStatusCode();

            string text;
            bool truncated;
            int? promptTokens;
            int? completionTokens;
            int? cachedPromptTokens = null;

            if (provider.Dialect == Wire.Ollama)
            {
                var parsed = await response.Content.ReadFromJsonAsync<OllamaResponse>(linked.Token)
                    ?? throw new InvalidOperationException(
                        $"Empty response from the Ollama API at '{provider.Endpoint}'.");

                text = parsed.Message?.Content ?? string.Empty;

                // Ollama's own word for the same thing OpenAI calls finish_reason.
                truncated = string.Equals(parsed.DoneReason, "length", StringComparison.Ordinal);
                promptTokens = parsed.PromptEvalCount;
                completionTokens = parsed.EvalCount;

                // Left null rather than reported as zero. Ollama does reuse a cached prefix — it is
                // most of why a second question on one session answers faster than the first — but
                // it publishes no count of it, and inventing a zero here would read as "the prefix
                // was not reused" when what happened is that nobody was asked.
            }
            else
            {
                var parsed = await response.Content.ReadFromJsonAsync<ChatResponse>(linked.Token)
                    ?? throw new InvalidOperationException(
                        $"Empty response from the chat-completions API at '{provider.Endpoint}'.");

                var choice = parsed.Choices.FirstOrDefault();

                text = choice?.Message.Content ?? string.Empty;
                truncated = string.Equals(choice?.FinishReason, "length", StringComparison.Ordinal);
                promptTokens = parsed.Usage?.PromptTokens;
                completionTokens = parsed.Usage?.CompletionTokens;
                cachedPromptTokens = parsed.Usage?.CachedPromptTokens;
            }

            // One line per call, at Information, because the question it answers is one nobody can
            // answer from the token totals: those count what was read, not what was paid for. The
            // prompt is deliberately arranged so its first ~2,700 tokens never change, and whether a
            // provider reuses them decides both what a question costs and whether trimming the
            // prompt further is worth anyone's time. A gateway that reports nothing logs a cached
            // count of "not reported", which is itself the answer for that provider.
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "The {Choice} model '{Model}' read {PromptTokens} prompt tokens ({CachedTokens} "
                    + "from cache) and wrote {CompletionTokens}.",
                    provider.Choice, provider.Model, promptTokens,
                    (object?)cachedPromptTokens ?? "not reported", completionTokens);
            }

            if (truncated)
            {
                // Logged here rather than left for the parse to trip over, because this is the one
                // failure whose cause is not visible in what came back: the reply looks like a model
                // that stopped mid-sentence, and the reason is that it had nowhere left to write.
                //
                // The prompt and completion counts are the diagnosis. Adding to roughly the context
                // window means the model filled it; well under means something else cut the reply
                // short, and the token ceiling is the next thing to look at.
                _logger.LogWarning(
                    "The {Choice} model '{Model}' at {Endpoint} was cut off after {PromptTokens} "
                    + "prompt and {CompletionTokens} completion tokens. Its reply is incomplete and "
                    + "will not parse. The context window asked for was {ContextTokens}; the schema "
                    + "prompt alone runs to several thousand tokens, so this needs to be well above "
                    + "it — set '{Section}:Local:ContextTokens', or raise it on the server "
                    + "(OLLAMA_CONTEXT_LENGTH) if this provider is not on Ollama's native API.",
                    provider.Choice, provider.Model, provider.Endpoint,
                    promptTokens, completionTokens,
                    provider.ContextTokens?.ToString() ?? "the server's default",
                    AssistantOptions.SectionName);
            }

            return (text, promptTokens, completionTokens, truncated, cachedPromptTokens);
        }
    }

    /// <summary>Everything one call needs to know about where it is going.</summary>
    /// <remarks>
    /// Built per call from options rather than resolved once in the constructor, because the choice
    /// arrives with the question. Its cost is two string concatenations and a Uri parse against a
    /// request that is about to spend seconds on a model.
    /// </remarks>
    private Provider Resolve(AssistantModelChoice choice) => choice switch
    {
        AssistantModelChoice.Local => ResolveLocal(choice),

        _ => new Provider(
            choice,
            Wire.OpenAi,
            Endpoint(_options.BaseUrl, "chat/completions"),
            _options.Model,
            _options.ApiKey,

            // The hosted gateway keeps its deadline. A remote call that hangs is a network fault
            // worth abandoning; a local one is a slow machine worth waiting for.
            TimeSpan.FromSeconds(_options.RequestTimeoutSeconds),
            MaxOutputTokens: _options.MaxOutputTokens,

            // Not ours to set: a hosted model's context is the provider's business, and it is
            // large enough that the schema prompt does not come close.
            ContextTokens: null,
            RequiresApiKey: true,
            ApiKeySetting: $"{AssistantOptions.SectionName}:ApiKey",

            // Empty by default, which omits the field: the configured model is not a reasoning one,
            // and an OpenAI-shaped API may reject a parameter it does not know rather than ignore
            // it. Point BaseUrl at a gateway whose model does think and this is the setting that
            // stops it — see AssistantOptions.ReasoningEffort.
            ReasoningEffort: string.IsNullOrWhiteSpace(_options.ReasoningEffort)
                ? null
                : _options.ReasoningEffort)
    };

    /// <summary>
    /// The local provider, in whichever dialect it was configured to speak.
    /// </summary>
    /// <remarks>
    /// Ollama's native API is the default because it is the only one that accepts a context size
    /// per request — the OpenAI-compatible endpoint takes the same field and ignores it, which is
    /// how a 4096-token default went unnoticed until it truncated every answer. See
    /// <see cref="LocalModelOptions.ContextTokens"/>.
    /// </remarks>
    private Provider ResolveLocal(AssistantModelChoice choice)
    {
        var native = _options.Local.Api.Equals("Ollama", StringComparison.OrdinalIgnoreCase);

        return new Provider(
            choice,
            native ? Wire.Ollama : Wire.OpenAi,
            native
                ? Endpoint(OllamaRoot(_options.Local.BaseUrl), "api/chat")
                : Endpoint(_options.Local.BaseUrl, "chat/completions"),
            _options.Local.Model,
            _options.Local.ApiKey,

            // Null is no deadline, which is the default. A negative value is read the same way
            // rather than clamped up to one second: DataAnnotations does not recurse into a nested
            // options object, so the [Range] on that setting is documentation, and turning a
            // nonsensical number into a one-second timeout would fail every local question in a way
            // nobody could diagnose.
            Timeout: _options.Local.RequestTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(_options.Local.RequestTimeoutSeconds)
                : null,

            // Null omits max_tokens entirely, leaving the ceiling to the local server. Nothing here
            // is metered, and a cap only buys a way to fail: it truncates the JSON wrapping the SQL
            // rather than the SQL, so the reply arrives unparseable after the full wait.
            MaxOutputTokens: _options.Local.MaxOutputTokens,

            // A local server authenticates nothing, so an operator who empties this means it. The
            // header is then omitted rather than sent blank.
            RequiresApiKey: false,
            ApiKeySetting: $"{AssistantOptions.SectionName}:Local:ApiKey",

            // What keeps the local model usable at all: qwen3.5:2b thinks by default, and a 2B
            // model on a CPU never finishes thinking within the token budget — it stops on length
            // with an empty content field. See LocalModelOptions.ReasoningEffort.
            ReasoningEffort: string.IsNullOrWhiteSpace(_options.Local.ReasoningEffort)
                ? null
                : _options.Local.ReasoningEffort,

            // The setting that decides whether the local model can answer at all, and the reason
            // the native API is the default: this is the one it accepts and the other ignores.
            ContextTokens: _options.Local.ContextTokens);
    }

    /// <summary>
    /// An endpoint under a provider root.
    /// </summary>
    /// <remarks>
    /// The trailing slash is added rather than required, because leaving it off is the easy mistake
    /// and its failure is silent: <c>Uri</c> resolves a relative path against the last segment of
    /// its base, so "http://localhost:11434/v1" + "chat/completions" is
    /// "http://localhost:11434/chat/completions" — a URL that exists nowhere and reads, at a
    /// glance, like the one that was meant.
    /// </remarks>
    private static Uri Endpoint(string baseUrl, string path) =>
        new(new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"), path);

    /// <summary>
    /// Ollama's root, given a base URL that may still point at its OpenAI-compatible surface.
    /// </summary>
    /// <remarks>
    /// The native API lives at the root and the OpenAI-compatible one under <c>/v1/</c>, and this
    /// setting used to have to be the latter. Left as written, a configuration carried over from
    /// then would ask for <c>/v1/api/chat</c> and get a 404 that says nothing about why. Stripping
    /// it is a guess, but it is the only guess that can be right: there is no <c>/v1/api/chat</c>
    /// to mean anything else.
    /// </remarks>
    private static string OllamaRoot(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');

        return trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^3] + "/"
            : trimmed + "/";
    }

    /// <summary>Which wire format a provider speaks.</summary>
    /// <remarks>
    /// Not a preference. Ollama exposes both, and only the native one accepts a context size per
    /// request — the OpenAI-compatible endpoint takes <c>options.num_ctx</c> and discards it, which
    /// leaves the model loaded at its 4096-token default and truncates every answer longer than a
    /// couple of hundred tokens.
    /// </remarks>
    private enum Wire
    {
        /// <summary>OpenAI's <c>/chat/completions</c>. The hosted gateway, and any server like it.</summary>
        OpenAi,

        /// <summary>Ollama's own <c>/api/chat</c>, which takes <c>options.num_ctx</c>.</summary>
        Ollama
    }

    /// <summary>Where one call is going, in what dialect, and under what limits.</summary>
    /// <remarks>
    /// The limits are nullable and null means absent rather than zero throughout: <c>Timeout</c>
    /// null is no deadline at all, <c>MaxOutputTokens</c> null leaves the ceiling to the server,
    /// and <c>ContextTokens</c> null leaves the context window as the server loaded it. The first
    /// two are how the local provider is configured by default; the third is emphatically not —
    /// see <see cref="LocalModelOptions.ContextTokens"/>.
    /// </remarks>
    private sealed record Provider(
        AssistantModelChoice Choice,
        Wire Dialect,
        Uri Endpoint,
        string Model,
        string ApiKey,
        TimeSpan? Timeout,
        int? MaxOutputTokens,
        bool RequiresApiKey,
        string ApiKeySetting,
        string? ReasoningEffort,
        int? ContextTokens);

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

    /// <summary>
    /// As much of a reply as is worth putting in a log line.
    /// </summary>
    /// <remarks>
    /// Capped because this goes into the log on a failure path, and an unbounded model reply is not
    /// something to hand a log sink verbatim. The cut is generous: what makes one of these
    /// diagnosable is seeing where the text stops, and a truncated reply that ends mid-token looks
    /// like a complete one if the excerpt itself ends first. The suffix says which is which.
    /// </remarks>
    private static string Excerpt(string text)
    {
        const int limit = 2000;

        if (string.IsNullOrEmpty(text))
        {
            return "<empty>";
        }

        return text.Length <= limit
            ? text
            : text[..limit] + $"… [{text.Length - limit} more characters]";
    }

    /// <summary>The request body, in OpenAI's chat-completions shape.</summary>
    /// <remarks>
    /// Two of these are dropped from the payload when null rather than sent as an explicit null,
    /// and <c>response_format</c> deliberately is not. The difference is whether every gateway
    /// understands the field: they all understand response_format, so a null there reads as "no
    /// JSON mode", while <c>max_tokens</c> absent means "your default" and a provider that has
    /// never heard of <c>reasoning_effort</c> is better off not seeing it at all.
    /// </remarks>
    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("max_tokens")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? MaxTokens,
        [property: JsonPropertyName("response_format")] ChatResponseFormat? ResponseFormat,
        [property: JsonPropertyName("reasoning_effort")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ReasoningEffort);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponseFormat(
        [property: JsonPropertyName("type")] string Type);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice> Choices,
        [property: JsonPropertyName("usage")] ChatUsage? Usage);

    /// <summary>One completion, and why the model stopped producing it.</summary>
    /// <remarks>
    /// <c>FinishReason</c> is <c>"stop"</c> for a complete reply and <c>"length"</c> for one cut
    /// off having run out of room — the difference between a model that answered badly and one
    /// that was not given space to answer at all.
    /// </remarks>
    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatMessage Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    /// <summary>
    /// How much of the prompt the provider served from its own cache rather than reading again.
    /// </summary>
    /// <remarks>
    /// Both spellings are read because the two gateways this client speaks to disagree, and which
    /// one answers is configuration rather than code: DeepSeek reports
    /// <c>prompt_cache_hit_tokens</c> at the top of <c>usage</c>, while OpenAI's shape puts
    /// <c>cached_tokens</c> inside <c>prompt_tokens_details</c>. Neither is mandatory, and a gateway
    /// that reports no caching at all leaves both absent — which reads as null, not zero, because
    /// "this provider does not say" and "nothing was cached" are different facts and only one of
    /// them is worth acting on.
    /// <para>
    /// This exists to answer a question the token count alone cannot: roughly 2,700 tokens of every
    /// prompt are a byte-identical prefix, deliberately arranged that way so a provider can reuse
    /// it, and until now nothing recorded whether any provider actually did. A cached prefix bills
    /// at a fraction of the input rate, so if it is being reused the effective cost of a question is
    /// a third of its token count — and further trimming of the prompt is close to pointless. If it
    /// is not, the ordering work bought nothing and that is worth knowing too.
    /// </para>
    /// </remarks>
    private sealed record ChatUsage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
        [property: JsonPropertyName("prompt_cache_hit_tokens")] int? PromptCacheHitTokens = null,
        [property: JsonPropertyName("prompt_tokens_details")] ChatPromptTokensDetails? PromptTokensDetails = null)
    {
        /// <summary>
        /// The cached prefix in whichever spelling the gateway used, or null if it said nothing.
        /// </summary>
        /// <remarks>
        /// The top-level field wins when both are present. They are two names for one number, so
        /// they cannot disagree meaningfully, and preferring the one the configured gateway actually
        /// documents keeps the reading closest to what is billed.
        /// </remarks>
        public int? CachedPromptTokens =>
            PromptCacheHitTokens ?? PromptTokensDetails?.CachedTokens;
    }

    /// <summary>OpenAI's nested spelling of the same cache figure.</summary>
    private sealed record ChatPromptTokensDetails(
        [property: JsonPropertyName("cached_tokens")] int? CachedTokens);

    // ----------------------------------------------------------------- Ollama's native shape

    /// <summary>The request body for Ollama's own <c>/api/chat</c>.</summary>
    /// <remarks>
    /// Close enough to OpenAI's to look interchangeable and different in every detail that matters
    /// here: <c>format</c> rather than <c>response_format</c>, <c>think</c> rather than
    /// <c>reasoning_effort</c>, generation settings under <c>options</c> rather than at the top
    /// level — and <c>stream</c> defaulting to true, which would hand back a sequence of JSON
    /// objects where this client expects one.
    /// <para>
    /// It is used in spite of that resemblance, not because of it. <see cref="OllamaOptions.NumCtx"/>
    /// is the only way to set the context window from here, and setting it is what makes the local
    /// model able to answer at all.
    /// </para>
    /// </remarks>
    private sealed record OllamaRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("format")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Format,
        [property: JsonPropertyName("think")] bool Think,
        [property: JsonPropertyName("options")] OllamaOptions Options);

    /// <param name="NumCtx">
    /// Context window in tokens. Null leaves the server's default, which for Ollama is 4096 — too
    /// small for this prompt by roughly a factor of four.
    /// </param>
    /// <param name="Temperature">Zero, for the same reason as the OpenAI path: reviewable output.</param>
    /// <param name="NumPredict">
    /// Ceiling on generated tokens. Null omits it, leaving Ollama's own default of no limit beyond
    /// the context window.
    /// </param>
    private sealed record OllamaOptions(
        [property: JsonPropertyName("num_ctx")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? NumCtx,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("num_predict")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? NumPredict);

    /// <summary>Ollama's reply. One object, because <c>stream</c> was false.</summary>
    /// <remarks>
    /// <c>DoneReason</c> is the native spelling of <c>finish_reason</c> and carries the same
    /// <c>"length"</c> for a reply that ran out of room. The token counts are named for what the
    /// runner did rather than for what was billed, since nothing here is.
    /// </remarks>
    private sealed record OllamaResponse(
        [property: JsonPropertyName("message")] ChatMessage? Message,
        [property: JsonPropertyName("done_reason")] string? DoneReason,
        [property: JsonPropertyName("prompt_eval_count")] int? PromptEvalCount,
        [property: JsonPropertyName("eval_count")] int? EvalCount);
}
