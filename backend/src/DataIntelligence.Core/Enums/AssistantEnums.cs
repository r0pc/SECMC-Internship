// backend/src/DataIntelligence.Core/Enums/AssistantEnums.cs
namespace DataIntelligence.Core.Enums;

/// <summary>
/// Why a generated statement was or was not allowed to run. Persisted as a string, matching
/// CK_AssistantQuery_Validation — renaming a member is a breaking schema change.
/// </summary>
public enum AssistantValidationOutcome
{
    Pending,
    Approved,

    /// <summary>The model did not produce a single SELECT statement.</summary>
    RejectedNotSelect,

    /// <summary>Referenced a table/view outside analytics.*.</summary>
    RejectedForbiddenObject,

    /// <summary>Multiple statements, batch separators, or unparseable SQL.</summary>
    RejectedSyntax,

    /// <summary>Exceeded the row/join/subquery complexity budget.</summary>
    RejectedComplexity,

    /// <summary>The model reported it could not answer from the schema at all.</summary>
    RejectedNoSql,

    /// <summary>
    /// The input was not a data question — a greeting, a thank-you, an empty thought.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="RejectedNoSql"/> so the rejected-query review queue stays worth
    /// reading. Both outcomes produce no SQL, but only one is interesting: "show me the password
    /// hashes" is a probe worth a human's attention, and "hi" is not. Filed together, the volume of
    /// the second buries the first, which defeats the point of keeping the queue.
    /// </remarks>
    NotADataQuestion,

    /// <summary>The model's response could not be read as the JSON it was asked for.</summary>
    /// <remarks>
    /// Also split from <see cref="RejectedNoSql"/>, for the opposite reason to
    /// <see cref="NotADataQuestion"/>: not because it is less interesting, but because it is a
    /// different kind of fact. RejectedNoSql records that the model judged the question
    /// unanswerable from the published views, and a reviewer reading a run of them looks for a
    /// missing view or a gap in the prompt. A provider that changes how it wraps its output would
    /// otherwise produce exactly that run while nothing is wrong with the schema at all, and send
    /// the reviewer looking in the one place the answer is not.
    /// </remarks>
    RejectedUnreadableResponse
}

public enum AssistantExecutionStatus
{
    Succeeded,
    Failed,
    Timeout,
    Cancelled
}

/// <summary>
/// Which model answers a question: the hosted one, or one running on this machine.
/// </summary>
/// <remarks>
/// Named for where the model runs rather than for who made it — <c>Cloud</c> rather than
/// <c>DeepSeek</c>, <c>Local</c> rather than <c>Ollama</c>. The gateway behind <c>Cloud</c> is a
/// configuration value precisely so it can be changed without code, and a member named after
/// today's provider would be a lie the first time it was. Where the request goes is the part that
/// does not change: off this machine, or not.
/// <para>
/// That distinction is also the one a user is actually choosing between. Local keeps every
/// question on the machine and costs nothing per token, and answers with a small model that writes
/// worse SQL; Cloud is the reverse. The exact model id each resolves to is recorded per turn
/// alongside this — see <c>ChatTranscriptTurn.ModelName</c> — so the audit trail says both which
/// kind was asked for and which model actually served it.
/// </para>
/// Persisted by name into the transcript document, like every other enum stored there. Renaming a
/// member silently changes the meaning of every turn already written.
/// </remarks>
public enum AssistantModelChoice
{
    /// <summary>The hosted gateway configured under <c>Assistant:BaseUrl</c>. The default.</summary>
    Cloud,

    /// <summary>A model served locally, configured under <c>Assistant:Local</c>.</summary>
    Local
}