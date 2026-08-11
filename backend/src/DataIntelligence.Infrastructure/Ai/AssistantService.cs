// backend/src/DataIntelligence.Infrastructure/Ai/AssistantService.cs
using DataIntelligence.Core;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DataIntelligence.Infrastructure.Ai;

/// <inheritdoc cref="IAssistantService"/>
/// <remarks>
/// Every step of FR-13 – FR-16 writes to <c>ai.AssistantQuery</c> before moving to the next step,
/// so a crash mid-flow still leaves an accurate record of how far the question got.
/// </remarks>
public sealed class AssistantService : IAssistantService
{
    /// <summary>
    /// What the assistant says when the question does not become a query — an unanswerable
    /// question, or an ordinary greeting.
    /// </summary>
    /// <remarks>
    /// Fixed text, and deliberately not a second trip to the model. Asking it to reply
    /// conversationally here is the one place the whole design leaks: with no result set in front
    /// of it, a model asked to be helpful about "what was CPI in June" will answer from memory,
    /// and a figure recalled from training data is indistinguishable in the UI from one this
    /// platform collected. FR-16 asks for "I cannot answer that from this data" *without*
    /// inventing numbers, which means the path that produced no data must not call the model
    /// again. Saying what it can answer is the useful part; saying it in fresh words is not.
    /// </remarks>
    internal const string CannotAnswer =
        "I can only answer from the data this platform collects: US consumer price index (CPI) "
        + "figures, SOFR daily rates, and the collection log. I couldn't turn that into a query "
        + "over them. Try, for example: \"What was CPI in June 2025?\", \"What is the average SOFR "
        + "rate in 2025?\", or \"Which sources failed to collect this week?\"";

    /// <summary>
    /// What the assistant says to a greeting or a pleasantry.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="CannotAnswer"/> because they answer different things. "Hi, how are
    /// you" is not a failed data question, and replying to it with "I couldn't turn that into a
    /// query" is both cold and untrue — it reads as a malfunction to someone who was being
    /// friendly.
    /// <para>
    /// Still fixed text, and still no second call to the model, for the reason the whole design
    /// rests on: a model invited to be conversational with no result set in front of it will
    /// answer "so how much has CPI risen?" from memory, and a figure recalled from training data is
    /// indistinguishable in the UI from one this platform collected. Warmth is free; improvising is
    /// not. This is the only reply on the whole path that is not derived from data, which is
    /// exactly why it must never contain any.
    /// </para>
    /// </remarks>
    internal const string Greeting =
        "Hello — I'm well, thank you for asking. I'm the assistant for this platform's collected "
        + "data: US consumer price index (CPI) figures, SOFR daily rates, and the collection log. "
        + "Ask me something like \"What was CPI in June 2025?\", \"What is the average SOFR rate "
        + "in 2025?\", \"How did CPI and SOFR move together in 2025?\", or \"Which sources failed "
        + "to collect this week?\"";

    /// <summary>
    /// What the assistant says when the model's reply could not be read at all.
    /// </summary>
    /// <remarks>
    /// Separate text from <see cref="CannotAnswer"/>, because that message makes a claim this case
    /// cannot support: it tells the user their question was outside what the platform holds, and
    /// invites them to rephrase. A user who asks a perfectly ordinary question about CPI and is
    /// told the platform does not cover it will believe the platform, and stop asking. Saying the
    /// request failed keeps the fault where it belongs and makes retrying the obvious next move.
    /// </remarks>
    internal const string ResponseUnreadable =
        "Something went wrong turning that into a query — the answer came back in a form I "
        + "couldn't read. This is a fault on our side, not a problem with your question. Please "
        + "try asking it again.";

    /// <summary>
    /// What the assistant says when the model was cut off before it finished writing.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="ResponseUnreadable"/>, whose advice is wrong here in the way that
    /// costs the most time: it says to try again, and trying again reproduces this exactly. The
    /// model did nothing wrong and the question is fine — there was no room left to answer in, and
    /// nothing the person asking can do will make room. So this one says what actually changes it,
    /// and points at the other model as the thing to do meanwhile.
    /// </remarks>
    internal const string ResponseTruncated =
        "That answer was cut off before it finished — the model ran out of room partway through "
        + "writing the query. Asking again will hit the same limit, so this needs its context "
        + "window raised (for a local model served by Ollama, start the server with "
        + "OLLAMA_CONTEXT_LENGTH set well above the size of the schema prompt). In the meantime, "
        + "the cloud model can answer this.";

    /// <summary>
    /// What CPI, SOFR and this platform are — the answers to "what is X", as opposed to "what was
    /// X".
    /// </summary>
    /// <remarks>
    /// Fixed text on this side, for the same reason as <see cref="Greeting"/> and no other: this is
    /// a reply produced without running a query, and the one rule the design cannot bend is that
    /// such a reply contains no figures. Every number here would be a number nobody collected.
    /// <para>
    /// What each says is drawn from what the platform actually holds — the series it collects, the
    /// publisher, the cadence — so the description and the data cannot disagree. Anything a
    /// definition would want that changes over time ("the latest rate is…") is deliberately absent;
    /// that is a question, and it has a query behind it.
    /// </para>
    /// </remarks>
    internal const string AboutCpi =
        "The Consumer Price Index measures the average change over time in the prices paid by "
        + "urban consumers for a basket of goods and services. This platform collects one series "
        + "from the US Bureau of Labor Statistics — CUUR0000SA0, \"All items in U.S. city average, "
        + "all urban consumers, not seasonally adjusted\" — published monthly, with the annual and "
        + "semiannual averages BLS publishes alongside it. The index is not a price in dollars: it "
        + "is a level against a 1982-84 base of 100, so what it is usually read for is the change "
        + "between two periods. Ask \"What was CPI in June 2025?\" for a level, or \"What was the "
        + "year over year inflation rate for the last 3 months?\" for the change.";

    internal const string AboutSofr =
        "The Secured Overnight Financing Rate is a broad measure of what it costs to borrow cash "
        + "overnight against US Treasury securities. It is published each business day by the "
        + "Federal Reserve Bank of New York, based on that day's actual transactions, and is the "
        + "reference rate US markets moved to after LIBOR. This platform collects the SOFR rate "
        + "itself along with its volume and percentile spread — one row per business day, so there "
        + "are no weekend or holiday figures. Ask \"What is the average SOFR rate in 2025?\", or "
        + "\"Between which months did SOFR change the most in 2025?\"";

    internal const string AboutPlatform =
        "This platform collects two published US economic series on a schedule and stores every "
        + "version it has ever seen, so a figure that is later revised does not overwrite the one "
        + "reported at the time. It holds US consumer price index (CPI) figures from the Bureau of "
        + "Labor Statistics, SOFR daily rates from the Federal Reserve Bank of New York, and a log "
        + "of every collection attempt including the ones that failed. I answer by turning your "
        + "question into a read-only SQL query against that data and showing you the query with "
        + "the answer — so everything I tell you is something this platform actually collected, "
        + "and if a question falls outside it I will say so rather than guess.";

    private readonly DataIntelligenceDbContext _db;
    private readonly INlToSqlClient _llm;
    private readonly ISchemaContextProvider _schemaContext;
    private readonly ISqlSafetyValidator _validator;
    private readonly ReadOnlySqlExecutor _executor;
    private readonly TimeProvider _timeProvider;
    private readonly int _historyTurns;
    private readonly int _maxSummaryRows;

    public AssistantService(
        DataIntelligenceDbContext db,
        INlToSqlClient llm,
        ISchemaContextProvider schemaContext,
        ISqlSafetyValidator validator,
        ReadOnlySqlExecutor executor,
        TimeProvider timeProvider,
        IOptions<AssistantOptions> options)
    {
        _db = db;
        _llm = llm;
        _schemaContext = schemaContext;
        _validator = validator;
        _executor = executor;
        _timeProvider = timeProvider;
        _historyTurns = options.Value.HistoryTurns;
        _maxSummaryRows = options.Value.MaxSummaryRows;
    }

    public async Task<AssistantAnswerDto> AskAsync(
        int userId, AskQuestionRequest request, string? clientIp, CancellationToken cancellationToken)
    {
        var now = PakistanTime.Now(_timeProvider);
        var session = await GetOrCreateSessionAsync(userId, request.SessionId, now, cancellationToken);

        // Stamped here rather than on the success path at the end, so a session stays current even
        // when every question in it is refused. It reads as "last used", and a conversation the
        // user is actively having — badly — is still one they are having.
        session.LastActivityAtPkt = now;

        // The document is the store, so the turns are read out of it, added to, and written back
        // whole. Two questions answered against one session at the same moment would both load
        // this list and the later save would win, dropping the other turn — see the class remarks.
        var turns = ChatTranscriptWriter.ReadTurns(session.TranscriptJson);

        var draft = new TurnDraft
        {
            AssistantQueryId = await NextTurnIdAsync(cancellationToken),
            SessionId = session.SessionId,
            AskedAtPkt = now,
            Question = request.Question,
            Outcome = AssistantValidationOutcome.Pending,
            ClientIpHash = HashIp(clientIp),

            // Recorded with the question rather than with the answer, so a turn that crashes
            // between the two still says which model was being asked when it did. That is the
            // first thing anyone reading a run of failures wants to know.
            ModelChoice = request.Model
        };

        // Logged before anything else can fail (FR-14). The turn is written as Pending and then
        // rewritten as it progresses, which is what the row insert used to do — a question that
        // crashes the process mid-answer is still on the record as having been asked.
        turns.Add(draft.ToTurn());
        await SaveTurnsAsync(session, turns, cancellationToken);

        var overallStopwatch = Stopwatch.StartNew();

        var schemaContext = await _schemaContext.GetContextAsync(cancellationToken);
        var history = BuildHistory(turns, draft.AssistantQueryId);

        var generation = await _llm.GenerateSqlAsync(
            request.Question, schemaContext, history, request.Model, cancellationToken);

        draft.ModelName = generation.ModelName;
        draft.PromptTokens = generation.PromptTokens;
        draft.CompletionTokens = generation.CompletionTokens;
        draft.Sql = generation.Sql;
        draft.Explanation = generation.Explanation;
        draft.Parameters = generation.Sql is null ? null : generation.Parameters;

        if (generation.Sql is null)
        {
            var (outcome, detail, answer) = generation.Refusal switch
            {
                NlRefusalKind.NotADataQuestion => (
                    AssistantValidationOutcome.NotADataQuestion,
                    "Not a question about data.",
                    Greeting),

                // Recorded as NotADataQuestion because that is what it is in SQL terms — no query
                // was needed — and because the review queue exists to surface refusals worth
                // reading. "What is SOFR?" answered correctly is not one of them.
                NlRefusalKind.AboutCpi => (
                    AssistantValidationOutcome.NotADataQuestion,
                    "Asked what CPI is, rather than what it was.",
                    AboutCpi),

                NlRefusalKind.AboutSofr => (
                    AssistantValidationOutcome.NotADataQuestion,
                    "Asked what SOFR is, rather than what it was.",
                    AboutSofr),

                NlRefusalKind.AboutPlatform => (
                    AssistantValidationOutcome.NotADataQuestion,
                    "Asked what this platform holds.",
                    AboutPlatform),

                NlRefusalKind.Unreadable => (
                    AssistantValidationOutcome.RejectedUnreadableResponse,
                    "The model's response could not be read as the JSON it was asked for.",
                    ResponseUnreadable),

                // The same outcome as Unreadable, because in SQL terms it is the same thing: no
                // statement came back. The detail is what differs, and it is the whole value —
                // "could not be read" sends a reviewer looking for a model that ignores its output
                // format, which is the one thing that is not wrong here.
                NlRefusalKind.Truncated => (
                    AssistantValidationOutcome.RejectedUnreadableResponse,
                    "The model ran out of room and its reply stopped mid-sentence, so there was no "
                    + "complete statement to read. Raise the context window for this model.",
                    ResponseTruncated),

                _ => (
                    AssistantValidationOutcome.RejectedNoSql,
                    "The model could not express this question against the published views.",
                    CannotAnswer)
            };

            draft.Outcome = outcome;
            draft.ValidationDetail = detail;
            draft.Answer = answer;
            draft.TotalLatencyMs = (int)overallStopwatch.ElapsedMilliseconds;

            return await FinishAsync(session, turns, draft, null, cancellationToken);
        }

        var validation = _validator.Validate(generation.Sql, generation.Parameters);
        draft.Outcome = validation.Outcome;
        draft.ValidationDetail = validation.Detail;

        // What the statement actually binds, which is not always what the model offered: a value
        // supplied for a placeholder it then did not write is dropped rather than refused. Recorded
        // in place of the model's list so the parameters shown beside an answer are the ones that
        // produced it — see SqlValidationResult.BoundParameters.
        var bound = validation.BoundParameters ?? generation.Parameters;
        draft.Parameters = bound;

        if (!validation.IsApproved)
        {
            draft.Answer = "That question would need a query I'm not permitted to run "
                + $"({validation.Detail}). Try rephrasing it.";
            draft.TotalLatencyMs = (int)overallStopwatch.ElapsedMilliseconds;

            return await FinishAsync(session, turns, draft, null, cancellationToken);
        }

        // Recorded as executed before it executes, so a statement that hangs or kills the process
        // is not left looking as though it never ran (FR-15).
        //
        // This used to be backed by CK_AssistantQuery_NoUnvalidatedRun, which made it impossible
        // for the database to accept WasExecuted = 1 against anything but an approved outcome.
        // A CHECK constraint cannot see inside a JSON document, so that backstop is gone and the
        // ordering below is now the only thing keeping the two in step. What still holds is the
        // part that actually prevents harm: the validator refuses the statement, and the executor
        // runs as a principal with SELECT on analytics.* and DENY on sec and ai.
        draft.WasExecuted = true;
        await SaveTurnsAsync(session, Replace(turns, draft), cancellationToken);

        var executionStopwatch = Stopwatch.StartNew();
        var execution = await _executor.ExecuteAsync(
            validation.NormalizedSql!, bound, cancellationToken);
        draft.ExecutionMs = (int)executionStopwatch.ElapsedMilliseconds;

        if (!execution.Succeeded)
        {
            draft.ExecutionStatus = execution.TimedOut
                ? AssistantExecutionStatus.Timeout
                : AssistantExecutionStatus.Failed;
            draft.ExecutionError = execution.ErrorMessage;
            draft.Answer = execution.TimedOut
                ? "That query took too long to run — try narrowing the date range."
                : "The query didn't run successfully against the database.";
            draft.TotalLatencyMs = (int)overallStopwatch.ElapsedMilliseconds;

            return await FinishAsync(session, turns, draft, null, cancellationToken);
        }

        draft.ExecutionStatus = AssistantExecutionStatus.Succeeded;
        draft.ResultRowCount = execution.Rows!.Count;

        // Columnar, and capped — see ResultSetFormatter. The rows handed back to the browser below
        // are the full set, unchanged: this shape exists for the model that has to describe them,
        // and only for it.
        var resultsJson = ResultSetFormatter.ToCompactJson(execution.Rows, _maxSummaryRows);

        // Only when there is nothing to describe. Coverage exists to tell one empty result from
        // another — "we never collected this" against "this period is before the series existed" —
        // and against rows that did come back it answers a question nobody asked, in a prompt that
        // is charged for every line. Fetching it costs a database round trip as well as the tokens,
        // so the common case now pays neither.
        var coverage = execution.Rows.Count == 0
            ? await _schemaContext.GetCoverageAsync(cancellationToken)
            : string.Empty;

        // The same model that wrote the statement summarises its results. Splitting the two would
        // make the recorded model name true of half the turn.
        var summary = await _llm.SummariseResultsAsync(
            request.Question, validation.NormalizedSql!, bound, resultsJson,
            coverage, request.Model, cancellationToken);

        draft.Answer = summary.AnswerText;

        // Both model calls of the turn, added together. Left null if generation reported nothing,
        // so an unknown count is not laundered into a confident one by the summary's arriving.
        draft.CompletionTokens = draft.CompletionTokens is null && summary.CompletionTokens is null
            ? null
            : (draft.CompletionTokens ?? 0) + (summary.CompletionTokens ?? 0);

        draft.TotalLatencyMs = (int)overallStopwatch.ElapsedMilliseconds;

        return await FinishAsync(session, turns, draft, execution.Rows, cancellationToken);
    }

    public async Task RecordFeedbackAsync(
        long assistantQueryId, AssistantFeedbackRequest request, CancellationToken cancellationToken)
    {
        // Checked against the transcripts, since there is no longer a row per turn. This is the
        // only thing standing between the feedback table and an orphan: the foreign key that used
        // to guarantee it cannot point into a JSON document.
        var exists = await _db.AssistantTurns
            .AnyAsync(t => t.AssistantQueryId == assistantQueryId, cancellationToken);

        if (!exists)
        {
            throw new KeyNotFoundException($"No assistant query with id {assistantQueryId}.");
        }

        var feedback = await _db.AssistantFeedback
            .FirstOrDefaultAsync(f => f.AssistantQueryId == assistantQueryId, cancellationToken);

        if (feedback is null)
        {
            feedback = new AssistantFeedback { AssistantQueryId = assistantQueryId };
            _db.AssistantFeedback.Add(feedback);
        }

        feedback.IsHelpful = request.IsHelpful;
        feedback.Comment = request.Comment;
        feedback.SubmittedAtPkt = PakistanTime.Now(_timeProvider);

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<AssistantQueryLogDto>> GetQueryLogAsync(
        AssistantQueryLogQuery query, CancellationToken cancellationToken)
    {
        var rows = _db.AssistantTurns.AsNoTracking();

        if (query.FromUtc is { } from)
        {
            rows = rows.Where(t => t.AskedAtPkt >= from);
        }

        if (query.ToUtc is { } to)
        {
            rows = rows.Where(t => t.AskedAtPkt <= to);
        }

        if (query.UserId is { } userId)
        {
            rows = rows.Where(t => t.UserId == userId);
        }

        if (query.RejectedOnly)
        {
            // The same predicate as before, but no longer index-backed: IX_AssistantQuery_Rejected
            // filtered a table, and there is no table left to filter. Every call now shreds every
            // transcript before this can be applied.
            rows = rows.Where(t =>
                t.ValidationOutcome != AssistantValidationOutcome.Approved
                && t.ValidationOutcome != AssistantValidationOutcome.Pending
                && t.ValidationOutcome != AssistantValidationOutcome.NotADataQuestion);
        }

        if (query.Outcome is { } outcome)
        {
            rows = rows.Where(t => t.ValidationOutcome == outcome);
        }

        var totalCount = await rows.CountAsync(cancellationToken);

        var page = await rows
            .OrderByDescending(t => t.AskedAtPkt)
            .ThenByDescending(t => t.AssistantQueryId)
            .Skip(query.Page.Skip)
            .Take(query.Page.PageSize)
            .ToListAsync(cancellationToken);

        var items = await AttachFeedbackAsync(page, cancellationToken);

        return PagedResult<AssistantQueryLogDto>.From(items, query.Page, totalCount);
    }

    public async Task<AssistantQueryLogDto?> GetQueryAsync(
        long assistantQueryId, CancellationToken cancellationToken)
    {
        var turn = await _db.AssistantTurns
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.AssistantQueryId == assistantQueryId, cancellationToken);

        if (turn is null)
        {
            return null;
        }

        var withFeedback = await AttachFeedbackAsync([turn], cancellationToken);

        return withFeedback[0];
    }

    public async Task<IReadOnlyList<AssistantSessionSummaryDto>> GetSessionsAsync(
        int userId, int limit, CancellationToken cancellationToken)
    {
        // Ordered by last activity rather than by when the conversation started: the one someone
        // wants back is almost always the one they were last in, which may be an old thread they
        // returned to this morning.
        var sessions = await _db.AssistantSessionSummaries
            .AsNoTracking()
            .Where(s => s.UserId == userId && s.TurnCount > 0)
            .OrderByDescending(s => s.LastActivityAtPkt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return sessions
            .Select(s => new AssistantSessionSummaryDto
            {
                SessionId = s.SessionId,
                StartedAtPkt = s.StartedAtPkt,
                LastActivityAtPkt = s.LastActivityAtPkt,
                TurnCount = s.TurnCount,
                Title = s.Title,
                TotalTokens = s.TotalTokens
            })
            .ToList();
    }

    public async Task<AssistantTranscriptDto?> GetTranscriptAsync(
        int userId, Guid sessionId, CancellationToken cancellationToken)
    {
        // The user id is part of the lookup, not a filter applied afterwards. A session id is a
        // bare GUID in a URL, and matching on it alone would hand any holder of one somebody
        // else's conversation.
        var session = await _db.AssistantSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.SessionId == sessionId && s.UserId == userId, cancellationToken);

        if (session?.TranscriptJson is null)
        {
            return null;
        }

        var transcript = ChatTranscriptWriter.Deserialize(session.TranscriptJson);

        if (transcript is null)
        {
            return null;
        }

        return new AssistantTranscriptDto
        {
            SessionId = session.SessionId,

            // Read off the session row, not the document. The header inside a transcript written
            // before the clock moved to PKT still holds a UTC reading under a legacy key, and the
            // columns beside it were shifted when the rest of the schema was.
            StartedAtPkt = session.StartedAtPkt,
            LastActivityAtPkt = session.LastActivityAtPkt,

            Turns = transcript.Turns.Select(ToTranscriptTurn).ToList()
        };
    }

    /// <summary>
    /// Narrows a stored turn to what the browser may see.
    /// </summary>
    /// <remarks>
    /// Written as an explicit projection rather than by returning the stored record, so that a
    /// field added to the audit document later does not silently start being served to clients.
    /// The client IP hash is the one that matters today.
    /// </remarks>
    private static AssistantTranscriptTurnDto ToTranscriptTurn(ChatTranscriptTurn t) => new()
    {
        AssistantQueryId = t.AssistantQueryId,
        AskedAtPkt = t.AskedAtPkt,
        Question = t.Question,
        Answer = t.Answer,
        Outcome = t.Outcome,

        // Hidden unless approved, matching what AskAsync returns live. A resumed conversation
        // should read exactly as it did when it was had.
        GeneratedSql = t.Outcome == AssistantValidationOutcome.Approved ? t.Sql : null,
        SqlParameters = t.Outcome == AssistantValidationOutcome.Approved ? t.Parameters : null,

        Explanation = t.Explanation,
        WasExecuted = t.WasExecuted,
        ResultRowCount = t.ResultRowCount,
        ModelChoice = t.ModelChoice,
        ModelName = t.ModelName
    };

    /// <summary>
    /// Joins each turn to its feedback, if any.
    /// </summary>
    /// <remarks>
    /// Done as a second query against the ids of one page rather than as a join in the shredding
    /// query. A join would make the optimiser choose a plan across a table and a CROSS APPLY over
    /// every transcript in the database, and the shape that loses is the one where it decides to
    /// shred everything before filtering. Two queries keep the expensive half bounded by the page.
    /// </remarks>
    private async Task<List<AssistantQueryLogDto>> AttachFeedbackAsync(
        IReadOnlyList<AssistantTurn> turns, CancellationToken cancellationToken)
    {
        if (turns.Count == 0)
        {
            return [];
        }

        var ids = turns.Select(t => t.AssistantQueryId).ToList();

        var feedback = await _db.AssistantFeedback
            .AsNoTracking()
            .Where(f => ids.Contains(f.AssistantQueryId))
            .ToDictionaryAsync(f => f.AssistantQueryId, cancellationToken);

        return turns
            .Select(t => ToLogDto(t, feedback.GetValueOrDefault(t.AssistantQueryId)))
            .ToList();
    }

    private static AssistantQueryLogDto ToLogDto(AssistantTurn t, AssistantFeedback? feedback) => new()
    {
        AssistantQueryId = t.AssistantQueryId,
        SessionId = t.SessionId,
        UserId = t.UserId,
        AskedAtPkt = t.AskedAtPkt,
        QuestionText = t.QuestionText,

        // Shown for rejected rows too, unlike the answer DTO: judging whether a refusal was
        // correct means reading the statement that was refused.
        GeneratedSql = t.GeneratedSql,
        SqlParameters = DeserializeParameters(t.SqlParametersJson),
        Explanation = t.Explanation,

        ValidationOutcome = t.ValidationOutcome,
        ValidationDetail = t.ValidationDetail,
        WasExecuted = t.WasExecuted,
        ExecutionStatus = t.ExecutionStatus,
        ExecutionError = t.ExecutionError,
        ResultRowCount = t.ResultRowCount,
        ExecutionMs = t.ExecutionMs,
        AnswerText = t.AnswerText,
        ModelChoice = t.ModelChoice,
        ModelName = t.ModelName,
        PromptTokens = t.PromptTokens,
        CompletionTokens = t.CompletionTokens,
        TotalTokens = t.TotalTokens,
        TotalLatencyMs = t.TotalLatencyMs,
        FeedbackIsHelpful = feedback?.IsHelpful,
        FeedbackComment = feedback?.Comment
    };

    /// <summary>
    /// Closes a turn: writes the final state of it into the transcript and shapes the reply.
    /// </summary>
    /// <remarks>
    /// Every exit from <see cref="AskAsync"/> comes through here, refusals included. A transcript
    /// updated only on the paths that produced an answer would be missing exactly the turns a
    /// reader is most likely to be looking for, and the user's own question would vanish from the
    /// conversation they just had.
    /// </remarks>
    private async Task<AssistantAnswerDto> FinishAsync(
        AssistantSession session,
        List<ChatTranscriptTurn> turns,
        TurnDraft draft,
        IReadOnlyList<IReadOnlyDictionary<string, object?>>? rows,
        CancellationToken cancellationToken)
    {
        var final = Replace(turns, draft);
        await SaveTurnsAsync(session, final, cancellationToken);

        return ToDto(draft, rows);
    }

    /// <summary>Swaps the draft's latest state into the turn list, in place.</summary>
    /// <remarks>
    /// Matched by id rather than by position. The turn is appended at the start of the request and
    /// rewritten several times as it progresses, and position is not a safe handle on it — a
    /// concurrent turn appended to the same session would shift it.
    /// </remarks>
    private static List<ChatTranscriptTurn> Replace(List<ChatTranscriptTurn> turns, TurnDraft draft)
    {
        var index = turns.FindIndex(t => t.AssistantQueryId == draft.AssistantQueryId);

        if (index < 0)
        {
            turns.Add(draft.ToTurn());
        }
        else
        {
            turns[index] = draft.ToTurn();
        }

        return turns;
    }

    /// <summary>Serialises the conversation onto the session and commits it.</summary>
    /// <remarks>
    /// The running token total is recomputed here rather than added to, and that is deliberate: a
    /// turn is written several times as it progresses — Pending, then again once the model has
    /// answered — so adding this turn's cost on each save would count the same tokens two or three
    /// times over. Recomputing from the turns cannot drift, and the turns are already in hand.
    /// </remarks>
    private async Task SaveTurnsAsync(
        AssistantSession session, List<ChatTranscriptTurn> turns, CancellationToken cancellationToken)
    {
        session.TranscriptJson = ChatTranscriptWriter.Serialize(session, turns);
        session.TotalTokens = SumTokens(turns);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// What the conversation has cost so far, or null if nothing is known about what it cost.
    /// </summary>
    /// <remarks>
    /// Null and zero say different things here — "no turn reported usage" against "the model was
    /// never called" — and the chat list shows them differently, so the distinction is kept rather
    /// than collapsed by <c>Sum()</c>, which would report 0 for both.
    /// <para>
    /// Turns that report nothing are skipped rather than treated as zero. That makes a partially
    /// reported conversation a floor rather than an exact figure, which is the honest reading:
    /// see <see cref="ChatTranscriptTurn.TotalTokens"/> for why a missing count is not a zero one.
    /// </para>
    /// </remarks>
    private static int? SumTokens(IReadOnlyList<ChatTranscriptTurn> turns)
    {
        var known = turns.Where(t => t.TotalTokens is not null).ToList();

        return known.Count == 0 ? null : known.Sum(t => t.TotalTokens!.Value);
    }

    /// <summary>
    /// Allocates the next turn id from <c>ai.AssistantTurnId</c>.
    /// </summary>
    /// <remarks>
    /// A sequence rather than a count of the turns already in the document, because the id has to
    /// be unique across every session: it is what the API returns and what the feedback endpoint
    /// takes, and per-session numbering would have two conversations both claiming turn 1. The
    /// identity column that used to do this job belonged to a table that is no longer written.
    /// </remarks>
    private async Task<long> NextTurnIdAsync(CancellationToken cancellationToken)
    {
        var allocated = await _db.Database
            .SqlQueryRaw<long>("SELECT NEXT VALUE FOR ai.AssistantTurnId AS Value")
            .ToListAsync(cancellationToken);

        return allocated[0];
    }

    /// <summary>
    /// The last few exchanges of this session that actually became a query, oldest first.
    /// </summary>
    /// <remarks>
    /// Read from the turns already in hand rather than queried: the whole conversation was loaded
    /// to be written back anyway, so a second trip to the database would fetch what is sitting in
    /// memory. This is the one place the JSON store is cheaper than the table it replaced.
    /// <para>
    /// Restricted to statements that ran and succeeded. A refused turn offers nothing to resolve a
    /// pronoun against, and one that failed validation or blew up in the database is a statement
    /// the platform has already judged wrong — replaying either as an example of what to produce
    /// invites the model to produce more of it.
    /// </para>
    /// The turn just appended for this question is excluded by id. It is still Pending with no SQL,
    /// so the filters would drop it anyway — but only by accident of when it is written, and a
    /// question that quietly became context for itself is a strange thing to leave to luck.
    /// </remarks>
    private IReadOnlyList<ConversationTurn> BuildHistory(
        IReadOnlyList<ChatTranscriptTurn> turns, long currentTurnId)
    {
        if (_historyTurns <= 0)
        {
            return [];
        }

        return turns
            .Where(t => t.AssistantQueryId != currentTurnId
                && t.Sql is not null
                && t.Outcome == AssistantValidationOutcome.Approved
                && t.ExecutionStatus == AssistantExecutionStatus.Succeeded)
            .TakeLast(_historyTurns)
            .Select(t => new ConversationTurn(
                t.Question,
                t.Sql!,
                t.Parameters ?? new Dictionary<string, object?>()))
            .ToList();
    }

    private async Task<AssistantSession> GetOrCreateSessionAsync(
        int userId, Guid? sessionId, DateTime now, CancellationToken cancellationToken)
    {
        if (sessionId is { } id)
        {
            var existing = await _db.AssistantSessions
                .FirstOrDefaultAsync(s => s.SessionId == id && s.UserId == userId, cancellationToken);

            if (existing is not null)
            {
                return existing;
            }
        }

        var session = new AssistantSession
        {
            SessionId = Guid.NewGuid(),
            UserId = userId,
            StartedAtPkt = now,
            LastActivityAtPkt = now
        };

        _db.AssistantSessions.Add(session);
        return session;
    }

    /// <summary>
    /// Reads the stored parameter bag back for the response. A row written before this column
    /// existed, or one that failed to serialise, returns null rather than throwing — the answer
    /// is not worth losing over the parameter list beside it.
    /// </summary>
    private static IReadOnlyDictionary<string, object?>? DeserializeParameters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Hashed, not raw (SOW 3 — Security). Base64, because it is stored in JSON now.</summary>
    private static string? HashIp(string? ip) =>
        string.IsNullOrWhiteSpace(ip)
            ? null
            : Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(ip)));

    private static AssistantAnswerDto ToDto(
        TurnDraft draft, IReadOnlyList<IReadOnlyDictionary<string, object?>>? rows) => new()
    {
        AssistantQueryId = draft.AssistantQueryId,
        SessionId = draft.SessionId,
        QuestionText = draft.Question,
        ValidationOutcome = draft.Outcome,
        GeneratedSql = draft.Outcome == AssistantValidationOutcome.Approved ? draft.Sql : null,
        SqlParameters = draft.Outcome == AssistantValidationOutcome.Approved ? draft.Parameters : null,
        Explanation = draft.Explanation,
        WasExecuted = draft.WasExecuted,
        ExecutionStatus = draft.ExecutionStatus,
        AnswerText = draft.Answer ?? string.Empty,
        Rows = rows,
        ResultRowCount = draft.ResultRowCount,

        // The choice is always known; the name only once the model has been reached. A turn that
        // never got that far reports which model was asked and nothing about which one served it,
        // because none did.
        ModelChoice = draft.ModelChoice,
        ModelName = draft.ModelName
    };

    /// <summary>
    /// A turn while it is still being answered.
    /// </summary>
    /// <remarks>
    /// Mutable, unlike the <see cref="ChatTranscriptTurn"/> it becomes. A turn is filled in across
    /// eight or so steps, several of which can end it, and threading an immutable record through
    /// that with <c>with</c> expressions would mean every step returning a new value that the next
    /// step has to be given — an easy place to update one copy and persist another. The record is
    /// what gets stored; this is the thing being assembled.
    /// </remarks>
    private sealed class TurnDraft
    {
        public required long AssistantQueryId { get; init; }
        public Guid SessionId { get; set; }
        public required DateTime AskedAtPkt { get; init; }
        public required string Question { get; init; }
        public required AssistantValidationOutcome Outcome { get; set; }
        public string? ClientIpHash { get; init; }

        public string? Answer { get; set; }
        public string? ValidationDetail { get; set; }
        public string? Sql { get; set; }
        public IReadOnlyDictionary<string, object?>? Parameters { get; set; }
        public string? Explanation { get; set; }

        public bool WasExecuted { get; set; }
        public AssistantExecutionStatus? ExecutionStatus { get; set; }
        public string? ExecutionError { get; set; }
        public int? ExecutionMs { get; set; }
        public int? ResultRowCount { get; set; }

        public required AssistantModelChoice ModelChoice { get; init; }
        public string? ModelName { get; set; }
        public int? PromptTokens { get; set; }
        public int? CompletionTokens { get; set; }
        public int? TotalLatencyMs { get; set; }

        public ChatTranscriptTurn ToTurn() => new()
        {
            AssistantQueryId = AssistantQueryId,
            AskedAtPkt = AskedAtPkt,
            Question = Question,
            Answer = Answer,
            Outcome = Outcome,
            ValidationDetail = ValidationDetail,
            Sql = Sql,
            Parameters = Parameters,
            Explanation = Explanation,
            WasExecuted = WasExecuted,
            ExecutionStatus = ExecutionStatus,
            ExecutionError = ExecutionError,
            ExecutionMs = ExecutionMs,
            ResultRowCount = ResultRowCount,
            ModelChoice = ModelChoice,
            ModelName = ModelName,
            PromptTokens = PromptTokens,
            CompletionTokens = CompletionTokens,

            // Null unless both halves are known — see ChatTranscriptTurn.TotalTokens. Adding a
            // known half to an unknown one would report a confident undercount.
            TotalTokens = PromptTokens is { } p && CompletionTokens is { } c ? p + c : null,

            TotalLatencyMs = TotalLatencyMs,
            ClientIpHash = ClientIpHash
        };
    }
}
