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
    /// <param name="history">
    /// Earlier turns of the same conversation, oldest first, so a question that only makes sense
    /// after the one before it — "and the year before that?" — can be resolved. Empty for the
    /// first question of a session.
    /// </param>
    Task<NlToSqlResult> GenerateSqlAsync(
        string question,
        string schemaContext,
        IReadOnlyList<ConversationTurn> history,
        CancellationToken cancellationToken);

    /// <summary>Summarises a result set in plain language, in answer to the original question.</summary>
    /// <param name="parameters">
    /// The values bound to <paramref name="generatedSql"/>. Needed because the question may not say
    /// what it is about: a follow-up asking for "the year before that" was resolved during
    /// generation, and the bound <c>@year</c> is the only place the answer's actual subject is
    /// written down.
    /// </param>
    /// <param name="coverage">
    /// The first and last date held per dataset. Turns an empty result from a dead end into an
    /// explanation: "no rows" and "that period is before the series begins" are the same JSON, and
    /// only this tells them apart.
    /// </param>
    Task<NlSummaryResult> SummariseResultsAsync(
        string question,
        string generatedSql,
        IReadOnlyDictionary<string, object?> parameters,
        string resultsJson,
        string coverage,
        CancellationToken cancellationToken);
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
    Unanswerable,

    /// <summary>
    /// The model's response could not be read — not JSON, or JSON without a usable <c>sql</c>
    /// field.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Unanswerable"/> because the two say opposite things about where
    /// the fault lies. Unanswerable is the model exercising judgement about the schema; this is the
    /// model failing to answer in the shape it was asked to. Folded together, a provider that
    /// starts wrapping its output differently after a version bump shows up in the review queue as
    /// a sudden run of questions the platform supposedly cannot answer, and the reviewer goes
    /// looking for missing views instead of a broken response format.
    /// </remarks>
    Unreadable,

    /// <summary>Asks what CPI is, rather than what it was.</summary>
    /// <remarks>
    /// "What is SOFR?" is a fair question and neither chatter nor unanswerable — answering it with
    /// a greeting, as this used to, reads as the assistant having misheard. It is separated from
    /// the others because the reply is a definition rather than a figure, and definitions are the
    /// one thing safe to state without querying: the platform knows what it collects.
    /// <para>
    /// The model classifies; it does not compose. The text is fixed on this side, so the path that
    /// answers without running a query still cannot produce a number — which is the invariant the
    /// whole design is built on.
    /// </para>
    /// </remarks>
    AboutCpi,

    /// <summary>Asks what SOFR is.</summary>
    AboutSofr,

    /// <summary>Asks what the collection log or this platform is.</summary>
    AboutPlatform
}

public sealed record NlSummaryResult(string AnswerText, int? CompletionTokens, int LatencyMs);

/// <summary>One earlier exchange in the same session: what was asked, and the query it became.</summary>
/// <remarks>
/// Deliberately carries the question and the statement, and <b>not</b> the answer the user was
/// shown. Replaying figures back into the prompt is the one way this design could start answering
/// from memory: a model that can see "CPI in 2022 was 292.655" written above has everything it
/// needs to satisfy a follow-up without querying anything, and a recalled number is
/// indistinguishable in the UI from a collected one. The prior SQL resolves every reference a
/// follow-up actually needs — "that year" is right there in the parameters — while leaving the
/// figures where they belong, in the database.
/// <para>
/// Only turns that produced a statement are worth replaying. A refused turn contributes nothing to
/// resolve against and would teach the model, by example, that refusing is a normal reply.
/// </para>
/// </remarks>
/// <param name="Question">The user's question, verbatim.</param>
/// <param name="Sql">The statement it became.</param>
/// <param name="Parameters">The values bound to that statement.</param>
public sealed record ConversationTurn(
    string Question,
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters);