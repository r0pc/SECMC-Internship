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
    /// The values the model lifted out of the statement, checked against the placeholders the
    /// statement actually uses. A placeholder with no value is a rejection — it would fail at the
    /// database. A value with no placeholder is dropped instead, and
    /// <see cref="SqlValidationResult.BoundParameters"/> is what survives.
    /// </param>
    SqlValidationResult Validate(string sql, IReadOnlyDictionary<string, object?>? parameters = null);
}

public sealed record SqlValidationResult(AssistantValidationOutcome Outcome, string? Detail, string? NormalizedSql)
{
    public bool IsApproved => Outcome == AssistantValidationOutcome.Approved;

    /// <summary>
    /// The values to bind, narrowed to the placeholders <see cref="NormalizedSql"/> actually uses.
    /// Null when the statement was not approved.
    /// </summary>
    /// <remarks>
    /// Usually the same set that went in. It differs when the model supplied a value for a
    /// placeholder it then did not write — a small model does this often, typically leaving
    /// <c>@from</c> and <c>@to</c> behind after settling on a single-date query.
    /// <para>
    /// That used to be a rejection, on the reasoning that the model had described one query and
    /// written another. It is not a safety property, which is why it no longer is one: an unused
    /// value is never placed in the statement, never parsed, and cannot affect what the statement
    /// reads. Every property that does matter — one SELECT, published views only, every placeholder
    /// bound, no literal concatenated in — is checked separately and still rejects. Weighed against
    /// that, refusing a correct statement over a leftover value costs a right answer to enforce
    /// tidiness.
    /// </para>
    /// <para>
    /// They are dropped rather than passed through because what is shown beside an answer has to be
    /// what ran. A parameter list containing values the statement never mentions invites the reader
    /// to work out how they constrained a result they did not touch.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, object?>? BoundParameters { get; init; }
}