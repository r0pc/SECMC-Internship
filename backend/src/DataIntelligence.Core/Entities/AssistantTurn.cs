// backend/src/DataIntelligence.Core/Entities/AssistantTurn.cs
using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Entities;

/// <summary>
/// One turn, shredded back out of <c>ai.AssistantSession.TranscriptJson</c> so the audit log can be
/// filtered, sorted and paged in SQL rather than in memory.
/// </summary>
/// <remarks>
/// Keyless and read-only: this is a view over the JSON, not a table. It exists because the review
/// queue's questions — "every rejection in the last week", "everything this user asked" — are
/// relational questions, and answering them by loading every transcript into the API and filtering
/// there would move the whole audit history across the wire to discard most of it.
/// <para>
/// The cost of the store being JSON is that no index can serve this. Each read shreds every
/// transcript in the table with OPENJSON, which is a scan whose width grows with the whole
/// conversation history rather than with the page being asked for. Under the previous design this
/// was an index seek on <c>IX_AssistantQuery_Rejected</c>.
/// </para>
/// </remarks>
public class AssistantTurn
{
    public long AssistantQueryId { get; set; }
    public Guid SessionId { get; set; }
    public int UserId { get; set; }
    public DateTime AskedAtPkt { get; set; }

    public string QuestionText { get; set; } = string.Empty;
    public string? AnswerText { get; set; }

    public AssistantValidationOutcome ValidationOutcome { get; set; }
    public string? ValidationDetail { get; set; }

    public string? GeneratedSql { get; set; }

    /// <summary>The parameter object, still as JSON — shredding it further has no reader.</summary>
    public string? SqlParametersJson { get; set; }

    public string? Explanation { get; set; }

    public bool WasExecuted { get; set; }
    public AssistantExecutionStatus? ExecutionStatus { get; set; }
    public string? ExecutionError { get; set; }
    public int? ExecutionMs { get; set; }
    public int? ResultRowCount { get; set; }

    public string? ModelName { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }
    public int? TotalLatencyMs { get; set; }
}

/// <summary>
/// One conversation reduced to what the "resume a chat" list needs, read straight out of the
/// transcript with JSON_VALUE.
/// </summary>
/// <remarks>
/// A separate read model from <see cref="AssistantTurn"/> because the two ask opposite things of
/// the same column. The turn view shreds every transcript into rows, which is right for the audit
/// log and ruinous here: listing a user's conversations would parse their whole chat history to
/// return one line each. JSON_VALUE reaches into the document for two scalars without
/// materialising the turns at all.
/// </remarks>
public class AssistantSessionSummary
{
    public Guid SessionId { get; set; }
    public int UserId { get; set; }
    public DateTime StartedAtPkt { get; set; }
    public DateTime LastActivityAtPkt { get; set; }

    /// <summary>How many exchanges the conversation holds.</summary>
    public int TurnCount { get; set; }

    /// <summary>The first question asked, used as the conversation's title.</summary>
    public string? Title { get; set; }

    /// <summary>
    /// Tokens the whole conversation has cost, read straight off
    /// <see cref="AssistantSession.TotalTokens"/>. Null when no turn reported usage.
    /// </summary>
    public int? TotalTokens { get; set; }
}
