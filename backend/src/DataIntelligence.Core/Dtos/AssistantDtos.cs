// backend/src/DataIntelligence.Core/Dtos/AssistantDtos.cs
using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Dtos;

public sealed record AskQuestionRequest
{
    public Guid? SessionId { get; init; }
    public required string Question { get; init; }

    /// <summary>
    /// Which model should answer. Defaults to <see cref="AssistantModelChoice.Cloud"/>, so a caller
    /// that omits it gets what every caller got before the choice existed.
    /// </summary>
    /// <remarks>
    /// Not nullable. An absent field and an explicit "Cloud" mean the same thing to the assistant,
    /// and a nullable field would invite code that treats the difference as meaningful somewhere
    /// downstream. What was actually used is recorded on the turn either way.
    /// </remarks>
    public AssistantModelChoice Model { get; init; } = AssistantModelChoice.Cloud;
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

    /// <summary>Which model answered, and what it is called there.</summary>
    /// <remarks>
    /// Returned with the answer for the same reason the SQL is: an answer whose author cannot be
    /// identified is one the reader has to take on trust. It also makes the choice legible after
    /// the fact — a chat that switched models halfway shows which turns came from where, rather
    /// than leaving the difference in quality to be explained by the reader.
    /// </remarks>
    public required AssistantModelChoice ModelChoice { get; init; }

    public string? ModelName { get; init; }
}

public sealed record AssistantFeedbackRequest
{
    public required bool IsHelpful { get; init; }
    public string? Comment { get; init; }
}

/// <summary>
/// One conversation, serialised into <c>ai.AssistantSession.TranscriptJson</c>.
/// </summary>
/// <remarks>
/// This is a stored document rather than a wire response, and the difference matters: once written
/// it outlives the code that wrote it. Fields may be added, but renaming or removing one silently
/// changes the meaning of every transcript already in the table, which no migration will fix.
/// </remarks>
public sealed record ChatTranscript
{
    public required Guid SessionId { get; init; }
    public required int UserId { get; init; }
    public required DateTime StartedAtPkt { get; init; }
    public required DateTime LastActivityAtPkt { get; init; }

    /// <summary>Stored alongside the turns so a reader can detect truncation without parsing them.</summary>
    public required int TurnCount { get; init; }

    public required IReadOnlyList<ChatTranscriptTurn> Turns { get; init; }
}

/// <summary>One exchange within a <see cref="ChatTranscript"/>.</summary>
/// <remarks>
/// Every turn is recorded, including the refused ones. A transcript that kept only the answered
/// questions would not be the conversation that happened — and a user rereading it would find
/// their own questions missing with no indication that they had ever been asked.
/// <para>
/// This carries the full audit record, not a readable summary of one. The transcript is the only
/// store: what is absent from this type is absent from the system, so every field the review queue
/// once read off <c>ai.AssistantQuery</c> is reproduced here.
/// </para>
/// </remarks>
public sealed record ChatTranscriptTurn
{
    /// <summary>
    /// Identifies the turn. Allocated from the <c>ai.AssistantTurnId</c> sequence rather than by
    /// an identity column, since there is no longer a row being inserted to allocate one.
    /// </summary>
    /// <remarks>
    /// Still named for the query it represents because it is the id the API returns and the
    /// feedback endpoint takes; renaming it would break both for no gain.
    /// </remarks>
    public required long AssistantQueryId { get; init; }

    public required DateTime AskedAtPkt { get; init; }
    public required string Question { get; init; }

    /// <summary>The reply as it was shown, including the text of a refusal.</summary>
    public string? Answer { get; init; }

    public required AssistantValidationOutcome Outcome { get; init; }

    /// <summary>Why validation turned the statement away, when it did.</summary>
    public string? ValidationDetail { get; init; }

    public string? Sql { get; init; }
    public IReadOnlyDictionary<string, object?>? Parameters { get; init; }
    public string? Explanation { get; init; }

    /// <summary>
    /// Whether the statement was run. Recorded rather than inferred from
    /// <see cref="Outcome"/>: approval is permission to run, and a turn can be approved and still
    /// never reach the database.
    /// </summary>
    public bool WasExecuted { get; init; }

    public AssistantExecutionStatus? ExecutionStatus { get; init; }
    public string? ExecutionError { get; init; }
    public int? ExecutionMs { get; init; }

    /// <summary>How many rows the answer was drawn from. The rows themselves are not stored.</summary>
    public int? ResultRowCount { get; init; }

    /// <summary>
    /// Which model was asked — the hosted one or the one on this machine.
    /// </summary>
    /// <remarks>
    /// Recorded per turn rather than per session, because it is chosen per question: a
    /// conversation can start on the local model, hit a question it writes bad SQL for, and finish
    /// on the hosted one. A session-level field would describe none of those turns correctly.
    /// <para>
    /// Kept alongside <see cref="ModelName"/> rather than instead of it, and the two answer
    /// different questions. The name says which model served the turn, which is what a reviewer
    /// comparing two answers needs; this says which of the two options the user picked, which is
    /// what survives a config change. Point <c>Assistant:Local:Model</c> at something else and
    /// every turn recorded before it keeps its own name and still reads as Local — where the name
    /// alone would leave "was this the local one?" to be answered by remembering what the setting
    /// used to be.
    /// </para>
    /// Null on turns written before this was recorded. That is the standing cost of a document
    /// store: there is no migration to backfill it, and an older turn genuinely does not say.
    /// </remarks>
    public AssistantModelChoice? ModelChoice { get; init; }

    /// <summary>The model id as its gateway spells it — <c>deepseek-v4-flash</c>, <c>qwen3.5:2b</c>.</summary>
    public string? ModelName { get; init; }

    /// <summary>Tokens the model was sent for this turn — the schema context dominates it.</summary>
    public int? PromptTokens { get; init; }

    /// <summary>
    /// Tokens the model produced, across both calls a turn can make: the statement, and the
    /// summary of its results.
    /// </summary>
    public int? CompletionTokens { get; init; }

    /// <summary>
    /// Prompt plus completion, and null unless <i>both</i> are known.
    /// </summary>
    /// <remarks>
    /// Stored rather than left to the reader to add up, because it is the number every billing
    /// question asks for and because the obvious way to compute it is wrong: treating a missing
    /// half as zero turns "we don't know" into a confident undercount, and a provider that omits
    /// usage on some responses would quietly deflate the total for the whole period.
    /// </remarks>
    public int? TotalTokens { get; init; }

    public int? TotalLatencyMs { get; init; }

    /// <summary>
    /// SHA-256 of the caller's IP address, base64 — never the address itself (SOW 3 — Security).
    /// </summary>
    /// <remarks>
    /// Kept because this document is now the audit record: the column that used to hold it is no
    /// longer written, and a field dropped here is a field the platform no longer has.
    /// </remarks>
    public string? ClientIpHash { get; init; }
}

/// <summary>One of a user's past conversations, as the "resume a chat" list shows it.</summary>
/// <remarks>
/// Carries no turns. The list is rendered before anyone has chosen a conversation, and pulling
/// every transcript to draw it would move the user's entire chat history across the wire to show a
/// dozen lines of it.
/// </remarks>
public sealed record AssistantSessionSummaryDto
{
    public required Guid SessionId { get; init; }
    public required DateTime StartedAtPkt { get; init; }
    public required DateTime LastActivityAtPkt { get; init; }
    public required int TurnCount { get; init; }

    /// <summary>
    /// The first question of the conversation, which is what it was about.
    /// </summary>
    /// <remarks>
    /// Taken rather than generated. A model-written title would be another call, another thing to
    /// get wrong, and another place a question could be paraphrased into something the user did
    /// not ask — and people recognise their own words faster than a summary of them.
    /// </remarks>
    public string? Title { get; init; }

    /// <summary>
    /// Tokens the whole conversation has cost the model, or null if none of its turns reported
    /// usage. See <see cref="Entities.AssistantSession.TotalTokens"/> for what the number counts.
    /// </summary>
    public int? TotalTokens { get; init; }
}

/// <summary>A past conversation, replayed into the chat view.</summary>
public sealed record AssistantTranscriptDto
{
    public required Guid SessionId { get; init; }
    public required DateTime StartedAtPkt { get; init; }
    public required DateTime LastActivityAtPkt { get; init; }
    public required IReadOnlyList<AssistantTranscriptTurnDto> Turns { get; init; }
}

/// <summary>One exchange within a replayed conversation.</summary>
/// <remarks>
/// Deliberately narrower than the stored <see cref="ChatTranscriptTurn"/>. The client IP hash is
/// audit data and never leaves the server, and the result rows were never stored — a resumed
/// conversation shows the answer that was given, not a re-run of the query behind it.
/// </remarks>
public sealed record AssistantTranscriptTurnDto
{
    public required long AssistantQueryId { get; init; }
    public required DateTime AskedAtPkt { get; init; }
    public required string Question { get; init; }
    public string? Answer { get; init; }
    public required AssistantValidationOutcome Outcome { get; init; }

    /// <summary>Null unless the query was approved, matching <see cref="AssistantAnswerDto"/>.</summary>
    public string? GeneratedSql { get; init; }

    public IReadOnlyDictionary<string, object?>? SqlParameters { get; init; }
    public string? Explanation { get; init; }
    public bool WasExecuted { get; init; }
    public int? ResultRowCount { get; init; }

    /// <summary>
    /// Which model answered this turn, null on turns recorded before the choice existed.
    /// </summary>
    /// <remarks>
    /// Unlike the live answer's, this is nullable — a replayed conversation reports what was
    /// stored, and an older transcript does not say. Defaulting it to Cloud here would invent a
    /// fact about a turn rather than admit the record is silent.
    /// </remarks>
    public AssistantModelChoice? ModelChoice { get; init; }

    public string? ModelName { get; init; }
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
    public required DateTime AskedAtPkt { get; init; }
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

    /// <summary>Which model was asked. Null on turns recorded before the choice existed.</summary>
    public AssistantModelChoice? ModelChoice { get; init; }

    public string? ModelName { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }

    /// <summary>Prompt plus completion, null unless both are known. See ChatTranscriptTurn.</summary>
    public int? TotalTokens { get; init; }

    public int? TotalLatencyMs { get; init; }

    public bool? FeedbackIsHelpful { get; init; }
    public string? FeedbackComment { get; init; }
}