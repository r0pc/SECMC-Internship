// backend/src/DataIntelligence.Api/Endpoints/AssistantEndpoints.cs
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Exceptions;
using DataIntelligence.Core.Interfaces;

namespace DataIntelligence.Api.Endpoints;

/// <summary>The AI query assistant (FR-13 – FR-17).</summary>
/// <remarks>
/// TODO: userId is hard-coded until FR-9 authentication lands; replace with the authenticated
/// user's id once that work merges.
/// </remarks>
public static class AssistantEndpoints
{
    /// <summary>
    /// Ceiling on one question, in words.
    /// </summary>
    /// <remarks>
    /// The character limit below it is a storage bound — <c>QuestionText</c> is shredded out of the
    /// transcript as <c>NVARCHAR(2000)</c>, and anything longer would be silently truncated in the
    /// audit log. This is a different thing: a question is one thing asked in one sentence or two,
    /// and past about a hundred words it is a paragraph carrying several. Those produce a statement
    /// answering some part of what was asked, which is worse than a refusal because the answer
    /// looks complete.
    /// <para>
    /// Enforced here rather than only in the browser. The frontend counts the same words and stops
    /// the round trip early, but this is the API's own rule and the only copy of it that a caller
    /// cannot skip.
    /// </para>
    /// </remarks>
    public const int MaxQuestionWords = 100;

    public static RouteGroupBuilder MapAssistantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/assistant").WithTags("Assistant");

        group.MapPost("/ask", async (
                AskQuestionRequest request,
                HttpContext http,
                IAssistantService assistant,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(request.Question))
                {
                    return ApiEndpoints.BadRequest("question is required.");
                }

                if (request.Question.Length > 2000)
                {
                    return ApiEndpoints.BadRequest("question must be 2000 characters or fewer.");
                }

                var words = CountWords(request.Question);

                if (words > MaxQuestionWords)
                {
                    // The count is reported back, not just the limit. "Too long" leaves the caller
                    // guessing how much to cut; "132 of 100" tells them.
                    return ApiEndpoints.BadRequest(
                        $"question must be {MaxQuestionWords} words or fewer; that one is {words}. "
                        + "Ask one thing at a time.");
                }

                var clientIp = http.Connection.RemoteIpAddress?.ToString();

                try
                {
                    var answer = await assistant.AskAsync(userId: 1, request, clientIp, cancellationToken);
                    return Results.Ok(answer);
                }
                catch (AssistantNotConfiguredException ex)
                {
                    // A deployment problem, not a bad request — say so, and say which setting.
                    return Results.Problem(
                        title: "Assistant not configured",
                        detail: ex.Message,
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            })
            .WithName("AskAssistant")
            .WithSummary("Asks the AI assistant a question about the collected data.")
            .WithDescription(
                "Every question is logged before its SQL is generated, validated, or executed "
                + "(NFR Auditability). Only a single read-only SELECT against the published "
                + "analytics views can ever run; anything else is rejected and explained back to "
                + "the caller rather than attempted. Questions are limited to "
                + $"{MaxQuestionWords} words. 'model' chooses which model answers — Cloud (the "
                + "hosted gateway, the default) or Local (a model served on the API's own "
                + "machine); whichever is used is recorded on the turn and returned with the "
                + "answer.")
            .Produces<AssistantAnswerDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapPost("/queries/{assistantQueryId:long}/feedback", async (
                long assistantQueryId,
                AssistantFeedbackRequest request,
                IAssistantService assistant,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    await assistant.RecordFeedbackAsync(assistantQueryId, request, cancellationToken);
                    return Results.NoContent();
                }
                catch (KeyNotFoundException ex)
                {
                    return ApiEndpoints.NotFound(ex.Message);
                }
            })
            .WithName("SubmitAssistantFeedback")
            .WithSummary("Thumbs up/down on one answer.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        MapSessionEndpoints(group);
        MapReviewEndpoints(group);

        return group;
    }

    /// <summary>
    /// Words in a question: runs of non-whitespace, however they are separated.
    /// </summary>
    /// <remarks>
    /// Deliberately the crudest definition that matches what a person counting would get, and the
    /// same one the frontend applies to the draft as it is typed. Anything cleverer — splitting
    /// hyphenated compounds, folding punctuation — would make the two disagree, and a browser that
    /// says 98 while the API says 101 turns a limit into a bug report.
    /// </remarks>
    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>
    /// Listing and reopening a user's own conversations.
    /// </summary>
    /// <remarks>
    /// The user id is hard-coded to 1 here exactly as it is on /ask, and every one of these calls
    /// is scoped by it. That scoping does nothing today — one placeholder user owns everything —
    /// and is written now rather than later because the day FR-9 makes it real is the day it has
    /// to already be right: an endpoint that returns a conversation by GUID alone hands one user
    /// another's chat history the moment there is more than one user.
    /// </remarks>
    private static void MapSessionEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/sessions", async (
                int? limit,
                IAssistantService assistant,
                CancellationToken cancellationToken) =>
            {
                // Clamped rather than rejected: a caller asking for 10,000 conversations means
                // "all of them", and a 400 helps nobody.
                var take = Math.Clamp(limit ?? 20, 1, 100);

                return Results.Ok(
                    await assistant.GetSessionsAsync(userId: 1, take, cancellationToken));
            })
            .WithName("GetAssistantSessions")
            .WithSummary("The caller's past conversations, most recently used first.")
            .WithDescription(
                "Enough to draw a 'resume a chat' list and no more: when the conversation started, "
                + "when it was last used, how many exchanges it holds, and its first question as a "
                + "title. Conversations that never got an answer are omitted. Fetch the turns "
                + "themselves from /assistant/sessions/{sessionId}.")
            .Produces<IReadOnlyList<AssistantSessionSummaryDto>>();

        group.MapGet("/sessions/{sessionId:guid}", async (
                Guid sessionId,
                IAssistantService assistant,
                CancellationToken cancellationToken) =>
            {
                var transcript = await assistant.GetTranscriptAsync(
                    userId: 1, sessionId, cancellationToken);

                // A conversation belonging to someone else is reported as missing, not as
                // forbidden. Telling a caller that a session exists but is not theirs is telling
                // them a session exists.
                return transcript is null
                    ? ApiEndpoints.NotFound($"No conversation with id {sessionId}.")
                    : Results.Ok(transcript);
            })
            .WithName("GetAssistantTranscript")
            .WithSummary("One past conversation, in full, for replaying into the chat.")
            .WithDescription(
                "Every turn in order — question, answer, outcome, and for approved turns the SQL "
                + "and its parameters. Result rows are not included: they were never stored, and "
                + "the answer text is what the user was actually shown.")
            .Produces<AssistantTranscriptDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// The audit-log review surface (NFR Auditability — "logged for review").
    /// </summary>
    /// <remarks>
    /// TODO: these must be restricted to an administrator once FR-9 lands. They are anonymous
    /// today because every endpoint is, and they expose more than the rest of the API does — the
    /// questions users asked and the SQL those produced. Restricting them is the first thing to do
    /// when roles exist, and this comment is the reminder.
    /// </remarks>
    private static void MapReviewEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/queries", async (
                DateTime? fromUtc,
                DateTime? toUtc,
                int? userId,
                bool? rejectedOnly,
                AssistantValidationOutcome? outcome,
                int? page,
                int? pageSize,
                IAssistantService assistant,
                CancellationToken cancellationToken) =>
            {
                var from = ApiEndpoints.NormalizeUtc(fromUtc);
                var to = ApiEndpoints.NormalizeUtc(toUtc);

                if (from is { } start && to is { } end && start > end)
                {
                    return ApiEndpoints.BadRequest($"'fromUtc' ({start:O}) is after 'toUtc' ({end:O}).");
                }

                var query = new AssistantQueryLogQuery
                {
                    FromUtc = from,
                    ToUtc = to,
                    UserId = userId,
                    RejectedOnly = rejectedOnly ?? false,
                    Outcome = outcome,
                    Page = PageRequest.Normalize(page, pageSize)
                };

                return Results.Ok(await assistant.GetQueryLogAsync(query, cancellationToken));
            })
            .WithName("GetAssistantQueryLog")
            .WithSummary("Every question the assistant was asked, newest first.")
            .WithDescription(
                "The audit log required by NFR Auditability. Rejected queries are included — they "
                + "are the security-interesting records — and each row carries the generated SQL, "
                + "its parameters, the model's explanation, and why it was or was not run. "
                + "rejectedOnly=true is the review queue: refusals worth a human's attention, "
                + "excluding input that was never a data question.")
            .Produces<PagedResult<AssistantQueryLogDto>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/queries/{assistantQueryId:long}", async (
                long assistantQueryId,
                IAssistantService assistant,
                CancellationToken cancellationToken) =>
            {
                var record = await assistant.GetQueryAsync(assistantQueryId, cancellationToken);

                return record is null
                    ? ApiEndpoints.NotFound($"No assistant query with id {assistantQueryId}.")
                    : Results.Ok(record);
            })
            .WithName("GetAssistantQuery")
            .WithSummary("One audit-log record in full.")
            .Produces<AssistantQueryLogDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }
}