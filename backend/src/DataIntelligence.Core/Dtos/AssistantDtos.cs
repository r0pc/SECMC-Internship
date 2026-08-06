// backend/src/DataIntelligence.Core/Dtos/AssistantDtos.cs
using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Dtos;

public sealed record AskQuestionRequest
{
    public Guid? SessionId { get; init; }
    public required string Question { get; init; }
}

/// <summary>What the assistant returns for one question (FR-16, FR-17).</summary>
public sealed record AssistantAnswerDto
{
    public required long AssistantQueryId { get; init; }
    public required Guid SessionId { get; init; }
    public required string QuestionText { get; init; }

    public required AssistantValidationOutcome ValidationOutcome { get; init; }

    /// <summary>Null when validation rejected the query before it ran.</summary>
    public string? GeneratedSql { get; init; }

    /// <summary>
    /// Values bound to the placeholders in <see cref="GeneratedSql"/>. Returned alongside rather
    /// than inlined, because the statement shown is the statement that ran (FR-14).
    /// </summary>
    public IReadOnlyDictionary<string, object?>? SqlParameters { get; init; }

    /// <summary>The model's own account of what the query does (FR-13).</summary>
    public string? Explanation { get; init; }

    public required bool WasExecuted { get; init; }
    public AssistantExecutionStatus? ExecutionStatus { get; init; }

    /// <summary>The natural-language answer, or an explanation of why none is available.</summary>
    public required string AnswerText { get; init; }

    /// <summary>Result rows, capped, for a chart or table the frontend can render (FR-17).</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>>? Rows { get; init; }

    public int? ResultRowCount { get; init; }
}

public sealed record AssistantFeedbackRequest
{
    public required bool IsHelpful { get; init; }
    public string? Comment { get; init; }
}

/// <summary>Filters over the assistant's audit log (NFR Auditability).</summary>
public sealed record AssistantQueryLogQuery
{
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public int? UserId { get; init; }

    /// <summary>
    /// Narrows to the queries the validator turned away and that are worth a human's attention —
    /// the review queue. Excludes greetings, which produce no SQL but are not findings.
    /// </summary>
    public bool RejectedOnly { get; init; }

    /// <summary>Narrows to one outcome exactly. Applied on top of <see cref="RejectedOnly"/>.</summary>
    public AssistantValidationOutcome? Outcome { get; init; }

    public PageRequest Page { get; init; } = PageRequest.Normalize(null, null);
}

/// <summary>One row of the audit log, as the review screen lists it.</summary>
/// <remarks>
/// Carries the generated SQL for every row, including rejected ones — a rejected statement is the
/// most important thing on this screen, and hiding it would leave a reviewer unable to judge
/// whether the refusal was right.
/// </remarks>
public sealed record AssistantQueryLogDto
{
    public required long AssistantQueryId { get; init; }
    public required Guid SessionId { get; init; }
    public required int UserId { get; init; }
    public required DateTime AskedAtUtc { get; init; }
    public required string QuestionText { get; init; }

    public string? GeneratedSql { get; init; }
    public IReadOnlyDictionary<string, object?>? SqlParameters { get; init; }
    public string? Explanation { get; init; }

    public required AssistantValidationOutcome ValidationOutcome { get; init; }
    public string? ValidationDetail { get; init; }

    public required bool WasExecuted { get; init; }
    public AssistantExecutionStatus? ExecutionStatus { get; init; }
    public string? ExecutionError { get; init; }
    public int? ResultRowCount { get; init; }
    public int? ExecutionMs { get; init; }

    public string? AnswerText { get; init; }
    public string? ModelName { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int? TotalLatencyMs { get; init; }

    public bool? FeedbackIsHelpful { get; init; }
    public string? FeedbackComment { get; init; }
}