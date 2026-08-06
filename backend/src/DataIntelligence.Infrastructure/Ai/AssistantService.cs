// backend/src/DataIntelligence.Infrastructure/Ai/AssistantService.cs
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

    private readonly DataIntelligenceDbContext _db;
    private readonly INlToSqlClient _llm;
    private readonly ISchemaContextProvider _schemaContext;
    private readonly ISqlSafetyValidator _validator;
    private readonly ReadOnlySqlExecutor _executor;
    private readonly TimeProvider _timeProvider;

    public AssistantService(
        DataIntelligenceDbContext db,
        INlToSqlClient llm,
        ISchemaContextProvider schemaContext,
        ISqlSafetyValidator validator,
        ReadOnlySqlExecutor executor,
        TimeProvider timeProvider)
    {
        _db = db;
        _llm = llm;
        _schemaContext = schemaContext;
        _validator = validator;
        _executor = executor;
        _timeProvider = timeProvider;
    }

    public async Task<AssistantAnswerDto> AskAsync(
        int userId, AskQuestionRequest request, string? clientIp, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var session = await GetOrCreateSessionAsync(userId, request.SessionId, now, cancellationToken);

        var log = new AssistantQuery
        {
            SessionId = session.SessionId,
            UserId = userId,
            AskedAtUtc = now,
            QuestionText = request.Question,
            ValidationOutcome = AssistantValidationOutcome.Pending,
            ClientIpHash = HashIp(clientIp)
        };

        _db.AssistantQueries.Add(log);
        await _db.SaveChangesAsync(cancellationToken); // Logged before anything else can fail (FR-14).

        var overallStopwatch = Stopwatch.StartNew();

        var schemaContext = await _schemaContext.GetContextAsync(cancellationToken);

        var generation = await _llm.GenerateSqlAsync(
            request.Question, schemaContext, cancellationToken);

        log.ModelName = generation.ModelName;
        log.PromptTokens = generation.PromptTokens;
        log.CompletionTokens = generation.CompletionTokens;
        log.GeneratedSql = generation.Sql;
        log.Explanation = generation.Explanation;

        // Serialised even when empty, so a reviewer can tell "this query took no parameters" from
        // "this row predates parameter capture".
        log.SqlParametersJson = generation.Sql is null
            ? null
            : JsonSerializer.Serialize(generation.Parameters);

        if (generation.Sql is null)
        {
            var chatter = generation.Refusal == NlRefusalKind.NotADataQuestion;

            log.ValidationOutcome = chatter
                ? AssistantValidationOutcome.NotADataQuestion
                : AssistantValidationOutcome.RejectedNoSql;
            log.ValidationDetail = chatter
                ? "Not a question about data."
                : "The model could not express this question against the published views.";
            log.AnswerText = CannotAnswer;
            log.TotalLatencyMs = (int)overallStopwatch.ElapsedMilliseconds;

            await _db.SaveChangesAsync(cancellationToken);
            return ToDto(log, null);
        }

        var validation = _validator.Validate(generation.Sql, generation.Parameters);
        log.ValidationOutcome = validation.Outcome;
        log.ValidationDetail = validation.Detail;

        if (!validation.IsApproved)
        {
            log.AnswerText = "That question would need a query I'm not permitted to run "
                + $"({validation.Detail}). Try rephrasing it.";
            log.TotalLatencyMs = (int)overallStopwatch.ElapsedMilliseconds;

            await _db.SaveChangesAsync(cancellationToken);
            return ToDto(log, null);
        }

        // Nothing runs without CK_AssistantQuery_NoUnvalidatedRun's own agreement, but this is the
        // point at which the app itself commits to executing — worth its own save (FR-15).
        log.WasExecuted = true;
        await _db.SaveChangesAsync(cancellationToken);

        var executionStopwatch = Stopwatch.StartNew();
        var execution = await _executor.ExecuteAsync(
            validation.NormalizedSql!, generation.Parameters, cancellationToken);
        log.ExecutionMs = (int)executionStopwatch.ElapsedMilliseconds;

        if (!execution.Succeeded)
        {
            log.ExecutionStatus = execution.TimedOut
                ? AssistantExecutionStatus.Timeout
                : AssistantExecutionStatus.Failed;
            log.ExecutionError = execution.ErrorMessage;
            log.AnswerText = execution.TimedOut
                ? "That query took too long to run — try narrowing the date range."
                : "The query didn't run successfully against the database.";
            log.TotalLatencyMs = (int)overallStopwatch.ElapsedMilliseconds;

            await _db.SaveChangesAsync(cancellationToken);
            return ToDto(log, null);
        }

        log.ExecutionStatus = AssistantExecutionStatus.Succeeded;
        log.ResultRowCount = execution.Rows!.Count;

        var resultsJson = JsonSerializer.Serialize(execution.Rows);
        var summary = await _llm.SummariseResultsAsync(
            request.Question, validation.NormalizedSql!, resultsJson, cancellationToken);

        log.AnswerText = summary.AnswerText;
        log.CompletionTokens = (log.CompletionTokens ?? 0) + (summary.CompletionTokens ?? 0);
        log.TotalLatencyMs = (int)overallStopwatch.ElapsedMilliseconds;

        session.LastActivityAtUtc = now;

        await _db.SaveChangesAsync(cancellationToken);

        return ToDto(log, execution.Rows);
    }

    public async Task RecordFeedbackAsync(
        long assistantQueryId, AssistantFeedbackRequest request, CancellationToken cancellationToken)
    {
        var exists = await _db.AssistantQueries
            .AnyAsync(q => q.AssistantQueryId == assistantQueryId, cancellationToken);

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
        feedback.SubmittedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<AssistantQueryLogDto>> GetQueryLogAsync(
        AssistantQueryLogQuery query, CancellationToken cancellationToken)
    {
        var rows = _db.AssistantQueries.AsNoTracking();

        if (query.FromUtc is { } from)
        {
            rows = rows.Where(q => q.AskedAtUtc >= from);
        }

        if (query.ToUtc is { } to)
        {
            rows = rows.Where(q => q.AskedAtUtc <= to);
        }

        if (query.UserId is { } userId)
        {
            rows = rows.Where(q => q.UserId == userId);
        }

        if (query.RejectedOnly)
        {
            // Matches IX_AssistantQuery_Rejected's filter exactly, so the review queue's default
            // view is an index seek rather than a scan over every question ever asked.
            rows = rows.Where(q =>
                q.ValidationOutcome != AssistantValidationOutcome.Approved
                && q.ValidationOutcome != AssistantValidationOutcome.Pending
                && q.ValidationOutcome != AssistantValidationOutcome.NotADataQuestion);
        }

        if (query.Outcome is { } outcome)
        {
            rows = rows.Where(q => q.ValidationOutcome == outcome);
        }

        var totalCount = await rows.CountAsync(cancellationToken);

        var page = await rows
            .OrderByDescending(q => q.AskedAtUtc)
            .ThenByDescending(q => q.AssistantQueryId)
            .Skip(query.Page.Skip)
            .Take(query.Page.PageSize)
            .Select(q => new { Query = q, q.Feedback })
            .ToListAsync(cancellationToken);

        var items = page.Select(r => ToLogDto(r.Query, r.Feedback)).ToList();

        return PagedResult<AssistantQueryLogDto>.From(items, query.Page, totalCount);
    }

    public async Task<AssistantQueryLogDto?> GetQueryAsync(
        long assistantQueryId, CancellationToken cancellationToken)
    {
        var row = await _db.AssistantQueries
            .AsNoTracking()
            .Where(q => q.AssistantQueryId == assistantQueryId)
            .Select(q => new { Query = q, q.Feedback })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : ToLogDto(row.Query, row.Feedback);
    }

    private static AssistantQueryLogDto ToLogDto(AssistantQuery q, AssistantFeedback? feedback) => new()
    {
        AssistantQueryId = q.AssistantQueryId,
        SessionId = q.SessionId,
        UserId = q.UserId,
        AskedAtUtc = q.AskedAtUtc,
        QuestionText = q.QuestionText,

        // Shown for rejected rows too, unlike the answer DTO: judging whether a refusal was
        // correct means reading the statement that was refused.
        GeneratedSql = q.GeneratedSql,
        SqlParameters = DeserializeParameters(q.SqlParametersJson),
        Explanation = q.Explanation,

        ValidationOutcome = q.ValidationOutcome,
        ValidationDetail = q.ValidationDetail,
        WasExecuted = q.WasExecuted,
        ExecutionStatus = q.ExecutionStatus,
        ExecutionError = q.ExecutionError,
        ResultRowCount = q.ResultRowCount,
        ExecutionMs = q.ExecutionMs,
        AnswerText = q.AnswerText,
        ModelName = q.ModelName,
        PromptTokens = q.PromptTokens,
        CompletionTokens = q.CompletionTokens,
        TotalLatencyMs = q.TotalLatencyMs,
        FeedbackIsHelpful = feedback?.IsHelpful,
        FeedbackComment = feedback?.Comment
    };

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
            StartedAtUtc = now,
            LastActivityAtUtc = now
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

    /// <summary>Hashed, not raw (SOW 3 — Security), matching AssistantQuery.ClientIpHash.</summary>
    private static byte[]? HashIp(string? ip) =>
        string.IsNullOrWhiteSpace(ip) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(ip));

    private static AssistantAnswerDto ToDto(
        AssistantQuery log, IReadOnlyList<IReadOnlyDictionary<string, object?>>? rows) => new()
    {
        AssistantQueryId = log.AssistantQueryId,
        SessionId = log.SessionId,
        QuestionText = log.QuestionText,
        ValidationOutcome = log.ValidationOutcome,
        GeneratedSql = log.ValidationOutcome == AssistantValidationOutcome.Approved ? log.GeneratedSql : null,
        SqlParameters = log.ValidationOutcome == AssistantValidationOutcome.Approved
            ? DeserializeParameters(log.SqlParametersJson)
            : null,
        Explanation = log.Explanation,
        WasExecuted = log.WasExecuted,
        ExecutionStatus = log.ExecutionStatus,
        AnswerText = log.AnswerText ?? string.Empty,
        Rows = rows,
        ResultRowCount = log.ResultRowCount
    };
}