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