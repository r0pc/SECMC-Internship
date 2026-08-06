// backend/src/DataIntelligence.Infrastructure/Ai/AssistantOptions.cs
using System.ComponentModel.DataAnnotations;

namespace DataIntelligence.Infrastructure.Ai;

public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    /// <summary>Never committed — user secrets or environment only (SOW 3).</summary>
    [Required]
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = "claude-sonnet-4-6";

    [Range(1, 300)]
    public int RequestTimeoutSeconds { get; set; } = 30;

    [Range(1, 60)]
    public int SqlExecutionTimeoutSeconds { get; set; } = 10;

    [Range(1, 4000)]
    public int MaxOutputTokens { get; set; } = 1024;
}