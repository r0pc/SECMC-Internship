// backend/src/DataIntelligence.Core/Interfaces/ISqlSafetyValidator.cs
using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Interfaces;

/// <summary>
/// The gate between whatever the model produced and the database (SOW 9 — unsafe AI SQL).
/// Nothing executes unless this approves it; CK_AssistantQuery_NoUnvalidatedRun is the backstop
/// if it is ever bypassed.
/// </summary>
public interface ISqlSafetyValidator
{
    SqlValidationResult Validate(string sql);
}

public sealed record SqlValidationResult(AssistantValidationOutcome Outcome, string? Detail, string? NormalizedSql)
{
    public bool IsApproved => Outcome == AssistantValidationOutcome.Approved;
}