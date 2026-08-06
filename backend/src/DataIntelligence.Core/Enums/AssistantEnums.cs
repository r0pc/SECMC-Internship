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
    RejectedNoSql
}

public enum AssistantExecutionStatus
{
    Succeeded,
    Failed,
    Timeout,
    Cancelled
}