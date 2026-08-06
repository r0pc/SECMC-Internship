// backend/src/DataIntelligence.Infrastructure/Ai/DeepSeekNlToSqlClient.cs
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataIntelligence.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace DataIntelligence.Infrastructure.Ai;

/// <summary>
/// Calls the DeepSeek chat completions API (OpenAI-compatible format) to turn a question into
/// SQL, and a result set into prose.
/// </summary>
/// <remarks>
/// DeepSeek's API mirrors OpenAI's <c>/chat/completions</c> shape rather than Anthropic's
/// <c>/messages</c> shape — a system message is just another entry in the messages array, and
/// JSON-only output is requested via <c>response_format</c> rather than prompt instructions
/// alone. Swapping providers again later means implementing <see cref="INlToSqlClient"/> once
/// more; nothing else in the assistant pipeline depends on this shape.
/// </remarks>
public sealed class DeepSeekNlToSqlClient : INlToSqlClient
{
    private readonly HttpClient _httpClient;
    private readonly AssistantOptions _options;

    public DeepSeekNlToSqlClient(HttpClient httpClient, IOptions<AssistantOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        _httpClient.BaseAddress ??= new Uri("https://api.deepseek.com/");
        _httpClient.DefaultRequestHeaders.Remove("Authorization");
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_options.ApiKey}");
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

        using var response = await _httpClient.PostAsJsonAsync("chat/completions", requestBody, linked.Token);
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