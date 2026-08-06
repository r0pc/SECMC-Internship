// backend/src/DataIntelligence.Infrastructure/Ai/AssistantOptions.cs
using System.ComponentModel.DataAnnotations;

namespace DataIntelligence.Infrastructure.Ai;

public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    /// <summary>
    /// Never committed — user secrets or environment only (SOW 3). Absent rather than
    /// <c>[Required]</c>: the API must still start and serve dashboards without it, so a missing
    /// key is reported by the assistant endpoint, not by a failed startup.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Provider root. Any gateway exposing OpenAI's <c>/chat/completions</c> shape works without a
    /// code change — a provider that does not means a new <see cref="INlToSqlClient"/>.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.deepseek.com/";

    /// <summary>
    /// A model id as the configured gateway spells it. DeepSeek's own API uses bare ids
    /// (<c>deepseek-v4-flash</c>); a reseller such as OpenRouter prefixes the vendor
    /// (<c>deepseek/deepseek-v4-flash</c>), and the two are not interchangeable.
    /// <c>GET {BaseUrl}models</c> lists what a key can actually reach.
    /// </summary>
    public string Model { get; set; } = "deepseek-v4-flash";

    [Range(1, 300)]
    public int RequestTimeoutSeconds { get; set; } = 30;

    [Range(1, 60)]
    public int SqlExecutionTimeoutSeconds { get; set; } = 10;

    [Range(1, 4000)]
    public int MaxOutputTokens { get; set; } = 1024;
}