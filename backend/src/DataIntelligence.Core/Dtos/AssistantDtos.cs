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