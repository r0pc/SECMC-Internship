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
    /// <summary>
    /// Decides whether a generated statement may run.
    /// </summary>
    /// <param name="sql">The statement exactly as the model produced it.</param>
    /// <param name="parameters">
    /// The values the model lifted out of the statement. Checked against the placeholders the
    /// statement actually uses: a placeholder with no value would fail at the database, and a
    /// value with no placeholder means the statement is not the one the model described.
    /// </param>
    SqlValidationResult Validate(string sql, IReadOnlyDictionary<string, object?>? parameters = null);
}

public sealed record SqlValidationResult(AssistantValidationOutcome Outcome, string? Detail, string? NormalizedSql)
{
    public bool IsApproved => Outcome == AssistantValidationOutcome.Approved;
}