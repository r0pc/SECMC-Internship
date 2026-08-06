// backend/src/DataIntelligence.Core/Interfaces/INlToSqlClient.cs
namespace DataIntelligence.Core.Interfaces;

/// <summary>
/// Turns a natural-language question into SQL against the read-only analytics schema, and turns
/// a result set back into a natural-language answer (FR-13, FR-16). Provider-agnostic: the
/// concrete implementation is whichever LLM API is configured.
/// </summary>
public interface INlToSqlClient
{
    /// <summary>
    /// Asks the model for one SELECT statement. <see cref="NlToSqlResult.Sql"/> is null when the
    /// model reports the question cannot be answered from the schema it was given — that is a
    /// legitimate outcome, not a failure of the call.
    /// </summary>
    Task<NlToSqlResult> GenerateSqlAsync(
        string question, string schemaContext, CancellationToken cancellationToken);

    /// <summary>Summarises a result set in plain language, in answer to the original question.</summary>
    Task<NlSummaryResult> SummariseResultsAsync(
        string question, string generatedSql, string resultsJson, CancellationToken cancellationToken);
}

/// <param name="Sql">
/// The statement, with literals lifted out into <paramref name="Parameters"/>. Null when the model
/// reported it cannot answer from the schema it was given.
/// </param>
/// <param name="Parameters">
/// Values for the placeholders in <paramref name="Sql"/>, keyed by name. Empty when the query needs
/// none. Kept separate from the statement all the way to <c>SqlCommand</c>, so a value that happens
/// to contain SQL is bound as data and never parsed (FR-14).
/// </param>
/// <param name="Explanation">
/// The model's own account of what it wrote, in plain language. Stored beside the SQL so the audit
/// trail records the intent as well as the statement (NFR Auditability).
/// </param>
/// <param name="Refusal">
/// Why there is no SQL. <see cref="NlRefusalKind.None"/> whenever <paramref name="Sql"/> is present.
/// </param>
public sealed record NlToSqlResult(
    string? Sql,
    IReadOnlyDictionary<string, object?> Parameters,
    string? Explanation,
    string ModelName,
    int? PromptTokens,
    int? CompletionTokens,
    int LatencyMs,
    NlRefusalKind Refusal = NlRefusalKind.None)
{
    /// <summary>The model declined to answer — a legitimate outcome, not a failed call.</summary>
    public static NlToSqlResult NoSql(
        NlRefusalKind refusal, string modelName, int? promptTokens, int? completionTokens, int latencyMs) =>
        new(null, new Dictionary<string, object?>(), null,
            modelName, promptTokens, completionTokens, latencyMs, refusal);
}

/// <summary>Why a question produced no statement.</summary>
/// <remarks>
/// The distinction exists for the audit trail rather than for the caller, who gets much the same
/// message either way: a probe at data it may not have is worth a reviewer's time, and a greeting
/// is not. See <c>AssistantValidationOutcome.NotADataQuestion</c>.
/// </remarks>
public enum NlRefusalKind
{
    None,

    /// <summary>Not a question about data at all — a greeting, thanks, or chatter.</summary>
    NotADataQuestion,

    /// <summary>A data question the published views cannot answer.</summary>
    Unanswerable
}

public sealed record NlSummaryResult(string AnswerText, int? CompletionTokens, int LatencyMs);