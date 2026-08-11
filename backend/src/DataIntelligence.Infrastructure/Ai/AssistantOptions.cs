// backend/src/DataIntelligence.Infrastructure/Ai/AssistantOptions.cs
using System.ComponentModel.DataAnnotations;

namespace DataIntelligence.Infrastructure.Ai;

public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    /// <summary>
    /// Never committed — user secrets or environment only (SOW 3). Absent rather than
    /// <c>[Required]</c>: the API must still start and serve dashboards without it, so a missing
    /// key is reported by the assistant endpoint, not by a failed startup.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Provider root. Any gateway exposing OpenAI's <c>/chat/completions</c> shape works without a
    /// code change — a provider that does not means a new <see cref="INlToSqlClient"/>.
    /// </summary>
    public string BaseUrl { get; set; } = "https://api.deepseek.com/";

    /// <summary>
    /// A model id as the configured gateway spells it. DeepSeek's own API uses bare ids
    /// (<c>deepseek-v4-flash</c>); a reseller such as OpenRouter prefixes the vendor
    /// (<c>deepseek/deepseek-v4-flash</c>), and the two are not interchangeable.
    /// <c>GET {BaseUrl}models</c> lists what a key can actually reach.
    /// </summary>
    public string Model { get; set; } = "deepseek-v4-flash";

    [Range(1, 300)]
    public int RequestTimeoutSeconds { get; set; } = 30;

    [Range(1, 60)]
    public int SqlExecutionTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Database user to <c>EXECUTE AS</c> before running generated SQL, when no dedicated
    /// read-only connection string is configured. Must belong to the <c>di_ai_readonly</c> role.
    /// </summary>
    /// <remarks>
    /// This exists because a Windows-authentication-only instance has no login to put in that
    /// role, so <c>ConnectionStrings:DataIntelligenceDbReadOnly</c> cannot be pointed anywhere
    /// meaningful. Impersonation reaches the same restricted principal over the app's own
    /// connection. Set neither and the assistant refuses to execute rather than falling back to
    /// the application's read-write rights.
    /// </remarks>
    public string? ExecuteAsUser { get; set; } = "di_ai_user";

    /// <summary>
    /// Ceiling on the model's reply to one call.
    /// </summary>
    /// <remarks>
    /// Raised from 1024, which was comfortable for a single-view lookup and too tight for the
    /// analytical shapes: "which month changed the most" nests three levels of derived table and
    /// runs to several hundred tokens of SQL before the explanation starts. Hitting the cap does
    /// not truncate the SQL into something invalid — it truncates the JSON wrapping it, so the
    /// reply arrives unparseable and the question is recorded as RejectedUnreadableResponse. The
    /// tell is a completion-token count sitting exactly on this number.
    /// <para>
    /// It is a ceiling, not a reservation: a short answer still costs short-answer tokens, so the
    /// headroom is only paid for when it is used.
    /// </para>
    /// </remarks>
    [Range(1, 4000)]
    public int MaxOutputTokens { get; set; } = 3000;

    /// <summary>
    /// How many earlier exchanges of the same session are replayed to the model, so a follow-up
    /// like "and the year before that?" can be resolved. Zero disables conversation memory.
    /// </summary>
    /// <remarks>
    /// Capped rather than unbounded because the schema context is already the larger part of every
    /// prompt, and each turn adds a question and a statement on top of it — a long session would
    /// otherwise grow the prompt until it crowded out the schema the model needs most. Six is two
    /// or three follow-ups deep, which is as far as a reference like "that year" realistically
    /// reaches back before the user restates what they mean.
    /// </remarks>
    [Range(0, 20)]
    public int HistoryTurns { get; set; } = 6;

    /// <summary>
    /// How many of those exchanges are replayed as their full statements. Anything older is
    /// summarised to one line — the question, the views it read, and the values it was bound to.
    /// </summary>
    /// <remarks>
    /// A replayed turn does two jobs, and they stop being worth the same after the second one. It
    /// supplies the referent a follow-up needs — which series, which period — and it demonstrates
    /// the output format, being literally the JSON the model produced. The referent survives the
    /// summary; the demonstration does not, and does not need to: a model handed the format
    /// specification and two worked examples learns nothing further from a sixth.
    /// <para>
    /// What it saves is the statements, which are the expensive half. A question answered with a
    /// nested LAG query runs to several hundred tokens of SQL, and every later question in the
    /// session used to pay for all of them again; the summary line for the same turn is a dozen or
    /// so. Set this to <see cref="HistoryTurns"/> to replay everything as before.
    /// </para>
    /// </remarks>
    [Range(0, 20)]
    public int VerbatimHistoryTurns { get; set; } = 2;

    /// <summary>
    /// The length at which a result is described to the model rather than listed for it. The user
    /// still sees every row that came back.
    /// </summary>
    /// <remarks>
    /// At or under this, every row is handed over. Over it, the model gets min, max and mean per
    /// column computed across <em>all</em> the rows, plus the five at each end — see
    /// <c>ResultSetFormatter</c>.
    /// <para>
    /// The threshold exists for accuracy first. A statement may return up to
    /// <c>SqlSafetyValidator.MaxRows</c>, and sending the first sixty of them is not a smaller
    /// version of the truth: asked how far SOFR moved over a year, a model shown sixty days answers
    /// for sixty days, and the real high is somewhere in the months it was not shown. The
    /// statistics are exact however long the series is. The tokens saved are the smaller half of
    /// the argument.
    /// </para>
    /// <para>
    /// Sixty is chosen against what these questions actually want. Anything asking for a figure, an
    /// average or an extreme is aggregated by the query itself and comes back as a handful of rows,
    /// which are listed as they always were; what returns hundreds is a series, and a few sentences
    /// about a series describe its shape rather than enumerate it. The cost of the choice is a
    /// question that genuinely wants a long list read out — "every day SOFR was above 5%" — which
    /// now gets a count, a range and both ends. Raise this where that is the common question.
    /// </para>
    /// </remarks>
    [Range(1, 2000)]
    public int MaxSummaryRows { get; set; } = 60;

    /// <summary>
    /// Sent to the hosted gateway as <c>reasoning_effort</c>. <c>"none"</c> turns the model's
    /// thinking off; empty omits the field entirely.
    /// </summary>
    /// <remarks>
    /// This defaulted to empty on the assumption that the configured model does not reason. Measured
    /// against the live gateway, that was wrong: <c>deepseek-v4-flash</c> reports
    /// <c>completion_tokens_details.reasoning_tokens</c>, and on one CPI question it spent 147 of
    /// its 208 completion tokens thinking before writing a statement essentially identical to the
    /// one it produced with thinking off — 54 completion tokens, and a shorter prompt too, since the
    /// gateway stops prepending its own reasoning scaffolding.
    /// <para>
    /// Completion tokens are the expensive half of a bill, so this is the rare setting that cuts
    /// cost without cutting anything the answer needed. Nothing in this pipeline benefits from
    /// deliberation: the question arrives with the schema, the rules and four worked examples in
    /// front of it, and the reply is one statement in a fixed shape.
    /// </para>
    /// <para>
    /// Set it back to empty for a gateway that rejects the parameter — an OpenAI-shaped API is
    /// within its rights to refuse a field it does not know rather than ignore it, and that is the
    /// one failure mode this default risks. The local model's equivalent is
    /// <see cref="LocalModelOptions.ReasoningEffort"/>, where the same setting is not an economy but
    /// the difference between answering and not.
    /// </para>
    /// </remarks>
    public string ReasoningEffort { get; set; } = "none";

    /// <summary>
    /// The alternative to the hosted gateway: a model served on the machine the API runs on,
    /// selected per question with <c>AssistantModelChoice.Local</c>.
    /// </summary>
    /// <remarks>
    /// Nested rather than flattened into more <c>Local*</c> keys beside the ones above, so the two
    /// providers read as two providers in <c>appsettings.json</c> instead of as one settings block
    /// with a second BaseUrl loose in it.
    /// <para>
    /// Note that <c>ValidateDataAnnotations</c> does not recurse into a nested object, so the
    /// annotations on <see cref="LocalModelOptions"/> describe its bounds rather than enforcing
    /// them. What enforces them is the client, which clamps what it reads.
    /// </para>
    /// </remarks>
    public LocalModelOptions Local { get; set; } = new();
}

/// <summary>
/// Where the locally-served model is, and what it is called there.
/// </summary>
/// <remarks>
/// Defaults describe Ollama with <c>qwen3.5:2b</c> pulled, which is the setup this was built
/// against. Ollama exposes OpenAI's <c>/v1/chat/completions</c> shape, so it is reached by the same
/// client as the hosted gateway — the local option is configuration, not a second implementation.
/// Any other server speaking that shape (llama.cpp, LM Studio, vLLM) is a <see cref="BaseUrl"/> and
/// <see cref="Model"/> change.
/// </remarks>
public sealed class LocalModelOptions
{
    /// <summary>
    /// Root of the local model server. For Ollama that is its bare root — <c>/v1/</c> is the
    /// OpenAI-compatible surface, and <see cref="Api"/> decides which of the two is used.
    /// </summary>
    /// <remarks>
    /// A trailing <c>/v1/</c> is stripped when <see cref="Api"/> is <c>Ollama</c>, rather than
    /// producing <c>/v1/api/chat</c> and a puzzling 404. This setting used to have to end in
    /// <c>/v1/</c>, so the forgiving reading is worth more than the strict one.
    /// </remarks>
    public string BaseUrl { get; set; } = "http://localhost:11434/";

    /// <summary>
    /// Which wire format the local server speaks: <c>Ollama</c> (the default, its native
    /// <c>/api/chat</c>) or <c>OpenAi</c> (any server exposing <c>/chat/completions</c>).
    /// </summary>
    /// <remarks>
    /// Ollama's native API is the default for one reason, and it is not preference: it is the only
    /// one of the two that lets this application set the context window. See
    /// <see cref="ContextTokens"/> for why that decides whether the local model works at all.
    /// Anything else — llama.cpp's server, LM Studio, vLLM — should be set to <c>OpenAi</c>, and
    /// then needs its context configured on the server side instead.
    /// </remarks>
    public string Api { get; set; } = "Ollama";

    /// <summary>
    /// The context window to load the model with, in tokens. Null leaves the server's default.
    /// </summary>
    /// <remarks>
    /// The single setting that decides whether the local model can answer at all, and the default
    /// of 4096 is too small by roughly a factor of four. The schema prompt alone runs to about
    /// 3,900 tokens before the question is added, and a context of 4096 leaves some 200 for the
    /// reply — so anything longer than a trivial statement stops mid-JSON, fails to parse, and is
    /// recorded as RejectedUnreadableResponse after a wait of minutes.
    /// <para>
    /// Measured on one question and one model, changing only this: at 4096 the reply stopped on
    /// <c>length</c> with the token total pinned at exactly 4096 and would not parse; at 16384 it
    /// finished and parsed. That is the whole of the difference.
    /// </para>
    /// <para>
    /// Sent as <c>options.num_ctx</c> on Ollama's native API, which is why that API is the default:
    /// its OpenAI-compatible endpoint accepts the same field and silently ignores it, so a
    /// deployment on <see cref="Api"/> <c>OpenAi</c> has to set this on the server instead —
    /// <c>OLLAMA_CONTEXT_LENGTH</c> for Ollama. Verify with <c>ollama ps</c>, whose CONTEXT column
    /// must not read 4096.
    /// </para>
    /// Raising it costs memory, since the KV cache is sized from it. 16384 is comfortable for a 2B
    /// model and leaves room for several turns of conversation on top of the schema.
    /// </remarks>
    public int? ContextTokens { get; set; } = 16384;

    /// <summary>
    /// The model as the local server names it, tag included. Ollama distinguishes <c>qwen3.5:4b</c>
    /// from <c>qwen3.5:2b</c> only by that tag, and a name it has not pulled is a 404 rather than a
    /// fallback. <c>ollama list</c> shows what is actually available.
    /// </summary>
    /// <remarks>
    /// Raised from <c>qwen3.5:2b</c>, which could not resolve a date the question stated. Asked
    /// "what is the cpi in june 2025" it queried the current month instead — measured against the
    /// prompt as it stands, against the longer prompt that preceded it, and against one carrying an
    /// explicit rule that a named period is absolute. All three produced a query for the wrong
    /// period, and one of them a range whose start was after its end. The 4B model, same prompt and
    /// same question, wrote <c>ReferenceDate = @month</c> with <c>@month = '2025-06-01'</c>.
    /// <para>
    /// The failure was worth more than the size difference because of what it looks like from the
    /// outside: a query for the wrong period returns nothing, and nothing is then explained as
    /// though the platform did not hold the data. The reader is told a figure does not exist when it
    /// does — see the summariser's instructions about a query that asked for a period the question
    /// did not name.
    /// </para>
    /// A 4B model is slower per question and holds more in memory, both of which are felt on a CPU.
    /// Set this back to <c>qwen3.5:2b</c> for a machine where that matters more than the answer.
    /// </remarks>
    public string Model { get; set; } = "qwen3.5:4b";

    /// <summary>
    /// Sent as a bearer token, and ignored by Ollama, which authenticates nothing.
    /// </summary>
    /// <remarks>
    /// Non-empty by default rather than blank because the value is irrelevant but its presence is
    /// not: some OpenAI-compatible servers reject a request with no Authorization header at all,
    /// and a placeholder costs nothing where a missing header costs a confusing 401. Unlike
    /// <see cref="AssistantOptions.ApiKey"/> this is not a secret and is safe to commit — nothing
    /// on the other end of a loopback address is being paid.
    /// </remarks>
    public string ApiKey { get; set; } = "ollama";

    /// <summary>
    /// Sent as <c>reasoning_effort</c>. <c>"none"</c> — the default — turns a reasoning model's
    /// thinking off. Empty omits the field entirely.
    /// </summary>
    /// <remarks>
    /// This is not a tuning knob; without it the local option does not work at all.
    /// <c>qwen3.5:2b</c> is a reasoning model, and Ollama returns its thinking in a separate
    /// <c>reasoning</c> field while <c>content</c> stays empty until the thinking finishes. A 2B
    /// model on a CPU does not finish: it spends the whole <c>MaxOutputTokens</c> budget deliberating
    /// over what to answer, stops on length with <c>content</c> still empty, and the turn is filed as
    /// RejectedUnreadableResponse minutes later. Measured on the machine this was built on, the same
    /// question took over three minutes and returned nothing with thinking on, and eighteen seconds
    /// and correctly-shaped JSON with it off.
    /// <para>
    /// Sent only to the local provider. The hosted gateway is not a reasoning model and has no use
    /// for the field, and an OpenAI-shaped API is within its rights to reject a parameter it does
    /// not know rather than ignore it.
    /// </para>
    /// <para>
    /// Note this is the field that works, not the one the Qwen documentation suggests:
    /// <c>chat_template_kwargs: {"enable_thinking": false}</c> is accepted by Ollama's
    /// OpenAI-compatible endpoint and has no effect on it. Both were tried.
    /// </para>
    /// </remarks>
    public string ReasoningEffort { get; set; } = "none";

    /// <summary>
    /// How long one call to the local model may take. <b>Zero — the default — means no limit.</b>
    /// </summary>
    /// <remarks>
    /// Unlimited rather than merely generous, because no number was the right one. A 2B model
    /// generating a few hundred tokens of SQL on a CPU is doing arithmetic this machine is not
    /// built for; the first call after startup also pays to load the weights; and how long any of
    /// that takes depends on the machine, what else is running on it, and how much schema context
    /// the prompt carries. Every ceiling tried was one some ordinary question ran past, and a
    /// question killed at the deadline costs the whole wait and returns nothing — strictly worse
    /// than waiting longer for an answer.
    /// <para>
    /// Waiting forever is only acceptable because it is not actually forever: the request's own
    /// cancellation token still applies, so a caller who navigates away or gives up takes the model
    /// call with them. What is removed is the arbitrary deadline, not the ability to stop.
    /// </para>
    /// <para>
    /// Nothing here applies to the hosted gateway, which keeps
    /// <see cref="AssistantOptions.RequestTimeoutSeconds"/>. A remote call that hangs is a network
    /// fault worth abandoning; a local one is a slow machine worth waiting for.
    /// </para>
    /// </remarks>
    [Range(0, 3600)]
    public int RequestTimeoutSeconds { get; set; } = 0;

    /// <summary>
    /// Ceiling on the local model's reply, in tokens. <b>Null — the default — means no limit</b>,
    /// leaving it to the local server, and omits <c>max_tokens</c> from the request entirely.
    /// </summary>
    /// <remarks>
    /// Overrides <see cref="AssistantOptions.MaxOutputTokens"/> for this provider only. That
    /// setting exists to bound what a metered API is asked to produce, and there is nothing to
    /// meter here: the tokens are free, the hardware is already paid for, and the only thing a cap
    /// buys is a way to fail. Hitting one does not truncate the SQL into something invalid — it
    /// truncates the JSON wrapping it, so the reply arrives unparseable and the question is
    /// recorded as RejectedUnreadableResponse after the full wait.
    /// <para>
    /// A small model is exactly the one most likely to need the headroom, since it is the one that
    /// rambles before it answers.
    /// </para>
    /// </remarks>
    public int? MaxOutputTokens { get; set; }
}