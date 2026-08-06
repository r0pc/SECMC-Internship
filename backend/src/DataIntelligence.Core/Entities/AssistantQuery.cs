// backend/src/DataIntelligence.Core/Entities/AssistantQuery.cs
using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Entities;

/// <summary>
/// One question asked of the assistant, and everything that happened to it — logged before
/// execution so a rejected query is still on the record (NFR Auditability).
/// </summary>
public class AssistantQuery
{
    public long AssistantQueryId { get; set; }
    public Guid SessionId { get; set; }

    /// <summary>Denormalised from the session: the audit trail must survive session cleanup.</summary>
    public int UserId { get; set; }

    public DateTime AskedAtUtc { get; set; }
    public string QuestionText { get; set; } = string.Empty;

    public string? GeneratedSql { get; set; }
    public string? SqlParametersJson { get; set; }
    public AssistantValidationOutcome ValidationOutcome { get; set; } = AssistantValidationOutcome.Pending;
    public string? ValidationDetail { get; set; }

    public bool WasExecuted { get; set; }
    public AssistantExecutionStatus? ExecutionStatus { get; set; }
    public int? ExecutionMs { get; set; }
    public int? ResultRowCount { get; set; }
    public string? ExecutionError { get; set; }

    public string? AnswerText { get; set; }
    public string? VisualizationJson { get; set; }

    public string? ModelName { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalLatencyMs { get; set; }
    public byte[]? ClientIpHash { get; set; }

    public AssistantSession? Session { get; set; }
    public AssistantFeedback? Feedback { get; set; }
}