// backend/src/DataIntelligence.Api/Endpoints/AssistantEndpoints.cs
using DataIntelligence.Core.Dtos;
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

        return group;
    }
}