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
        string question, string schemaContext, CancellationToken cancellationToken)
    {
        var system = schemaContext
            + "\n\nRespond with JSON only, no prose, in this exact shape: "
            + "{\"sql\": \"<statement or null>\"}";

        var stopwatch = Stopwatch.StartNew();

        var response = await SendAsync(system, question, jsonMode: true, cancellationToken);
        var json = ExtractJson(response.Text);

        string? sql = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("sql", out var sqlElement)
                && sqlElement.ValueKind == JsonValueKind.String)
            {
                sql = sqlElement.GetString();
            }
        }
        catch (JsonException)
        {
            sql = null; // Malformed model output becomes RejectedNoSql downstream, not a crash.
        }

        return new NlToSqlResult(
            string.IsNullOrWhiteSpace(sql) || sql == "null" ? null : sql,
            _options.Model, response.PromptTokens, response.CompletionTokens,
            (int)stopwatch.ElapsedMilliseconds);
    }

    public async Task<NlSummaryResult> SummariseResultsAsync(
        string question, string generatedSql, string resultsJson, CancellationToken cancellationToken)
    {
        const string system =
            "You answer questions about US economic data (CPI, SOFR) from a JSON result set. "
            + "Be concise, cite the actual figures, and never invent a number that is not in the "
            + "data given to you. If the result set is empty, say so plainly.";

        var user = $"Question: {question}\n\nSQL used: {generatedSql}\n\nResults (JSON): {resultsJson}";

        var stopwatch = Stopwatch.StartNew();
        var response = await SendAsync(system, user, jsonMode: false, cancellationToken);

        return new NlSummaryResult(response.Text.Trim(), response.CompletionTokens, (int)stopwatch.ElapsedMilliseconds);
    }

    private async Task<(string Text, int? PromptTokens, int? CompletionTokens)> SendAsync(
        string system, string userMessage, bool jsonMode, CancellationToken cancellationToken)
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