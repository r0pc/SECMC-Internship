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
                + "the caller rather than attempted.")
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

        MapReviewEndpoints(group);

        return group;
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