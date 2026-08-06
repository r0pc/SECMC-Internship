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
    private readonly DataIntelligenceDbContext _db;
    private readonly INlToSqlClient _llm;
    private readonly ISqlSafetyValidator _validator;
    private readonly ReadOnlySqlExecutor _executor;
    private readonly TimeProvider _timeProvider;

    public AssistantService(
        DataIntelligenceDbContext db,
        INlToSqlClient llm,
        ISqlSafetyValidator validator,
        ReadOnlySqlExecutor executor,
        TimeProvider timeProvider)
    {
        _db = db;
        _llm = llm;
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

        var generation = await _llm.GenerateSqlAsync(
            request.Question, SchemaContextProvider.Context, cancellationToken);

        log.ModelName = generation.ModelName;
        log.PromptTokens = generation.PromptTokens;
        log.CompletionTokens = generation.CompletionTokens;
        log.GeneratedSql = generation.Sql;

        if (generation.Sql is null)
        {
            log.ValidationOutcome = AssistantValidationOutcome.RejectedNoSql;
            log.ValidationDetail = "The model could not express this question against the published views.";
            log.AnswerText = "I couldn't turn that into a query against the data I have access to. "
                + "Try asking about CPI, SOFR, or collection health specifically.";
            log.TotalLatencyMs = (int)overallStopwatch.ElapsedMilliseconds;

            await _db.SaveChangesAsync(cancellationToken);
            return ToDto(log, null);
        }

        var validation = _validator.Validate(generation.Sql);
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
        var execution = await _executor.ExecuteAsync(validation.NormalizedSql!, cancellationToken);
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
        WasExecuted = log.WasExecuted,
        ExecutionStatus = log.ExecutionStatus,
        AnswerText = log.AnswerText ?? string.Empty,
        Rows = rows,
        ResultRowCount = log.ResultRowCount
    };
}