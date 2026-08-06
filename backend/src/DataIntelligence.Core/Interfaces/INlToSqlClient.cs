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

public sealed record NlToSqlResult(
    string? Sql,
    string ModelName,
    int? PromptTokens,
    int? CompletionTokens,
    int LatencyMs);

public sealed record NlSummaryResult(string AnswerText, int? CompletionTokens, int LatencyMs);