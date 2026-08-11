// backend/src/DataIntelligence.Infrastructure/Ai/AssistantPlanCache.cs
using System.Security.Cryptography;
using System.Text;
using DataIntelligence.Core.Enums;
using Microsoft.Extensions.Caching.Memory;

namespace DataIntelligence.Infrastructure.Ai;

/// <summary>
/// Remembers the statement a question became, so asking it again does not buy the same statement
/// twice.
/// </summary>
/// <remarks>
/// This is memoisation, not a guess. Both providers are called at temperature 0
/// (<c>ChatCompletionsNlToSqlClient</c> sets it on every request), which makes the model a
/// deterministic function of its input: the same prompt returns the same statement. Storing that
/// output against its input therefore cannot produce an answer the model would not have produced —
/// it removes a call whose result was already known, and nothing else.
/// <para>
/// What is cached is the <b>statement</b>, never the answer. A hit still validates the SQL, still
/// executes it against the database as the read-only principal, and still summarises the rows that
/// come back. The rule the whole design rests on — every figure comes from a query run now — is
/// untouched, because the query is run now. What is skipped is the model call that would have
/// written a statement identical to the one already held.
/// </para>
/// <para>
/// The saving is the largest available: the entire generation call, prompt and completion, plus its
/// latency. On the hosted gateway the prompt is nearly all cached prefix and bills at a fraction,
/// but the completion is not, and locally the whole call is seconds to minutes of CPU. On a repeated
/// question this removes all of it.
/// </para>
/// </remarks>
public sealed class AssistantPlanCache
{
    /// <summary>
    /// How long a remembered statement stays usable.
    /// </summary>
    /// <remarks>
    /// Short, because the key already invalidates on everything that could change the answer — see
    /// <see cref="Key"/>. The expiry is not the correctness mechanism, it is a bound on how long a
    /// process holds statements nobody has asked for again, and an hour is generous for that.
    /// </remarks>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly IMemoryCache _cache;

    public AssistantPlanCache(IMemoryCache cache) => _cache = cache;

    /// <summary>The statement a question became, if the same question has been answered recently.</summary>
    public CachedPlan? Find(string question, string schemaContext, AssistantModelChoice model) =>
        _cache.TryGetValue(Key(question, schemaContext, model), out CachedPlan? plan) ? plan : null;

    /// <summary>
    /// Remembers a statement that validated and ran. Only those: a statement the validator refused
    /// or the database rejected is one this platform has already judged wrong, and serving it again
    /// from memory would repeat the failure faster rather than avoid it.
    /// </summary>
    public void Remember(
        string question, string schemaContext, AssistantModelChoice model, CachedPlan plan) =>
        _cache.Set(Key(question, schemaContext, model), plan, Lifetime);

    /// <summary>
    /// What makes two questions the same question.
    /// </summary>
    /// <remarks>
    /// The whole schema context goes into the key, and that is the point rather than an excess of
    /// caution: it already contains today's date, the coverage window and the view list, so hashing
    /// it covers every input that could make the model answer differently. "What was inflation last
    /// month?" keys differently tomorrow because the date inside it moved; "what is the latest CPI
    /// figure" keys differently after a collection run because the coverage moved; a view gaining a
    /// column invalidates everything, which is correct, because a statement written against the old
    /// column list may no longer be the best one.
    /// <para>
    /// The model choice is in the key because the two models are not interchangeable in quality. A
    /// user who switches to the cloud model after a poor local answer is asking for that model's
    /// work, and handing back the local model's statement would silently deny them the thing they
    /// asked for.
    /// </para>
    /// <para>
    /// The question is normalised only for case and whitespace. Nothing cleverer: two questions that
    /// differ in wording may want different statements, and the cost of missing a hit is one model
    /// call, which is what would have happened anyway.
    /// </para>
    /// </remarks>
    private static string Key(string question, string schemaContext, AssistantModelChoice model)
    {
        var normalised = string.Join(' ', question.ToLowerInvariant().Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var material = $"{normalised}\n{(int)model}\n{schemaContext}";

        return "assistant-plan:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}

/// <summary>
/// A statement that answered this question before, with everything needed to run it again.
/// </summary>
/// <remarks>
/// Deliberately carries no rows, no answer text and no token counts. The rows are re-read from the
/// database and the answer re-written from them, so storing either would be storing a figure — and
/// a figure served from memory is the one thing this pipeline must never do. The token counts belong
/// to the call that is being skipped, and reporting them again would bill a call nobody made.
/// </remarks>
/// <param name="ModelName">
/// The model that originally wrote this statement, kept so the audit record still says what produced
/// it rather than attributing it to whatever is configured now.
/// </param>
public sealed record CachedPlan(
    string Sql,
    IReadOnlyDictionary<string, object?> Parameters,
    string? Explanation,
    string ModelName);
