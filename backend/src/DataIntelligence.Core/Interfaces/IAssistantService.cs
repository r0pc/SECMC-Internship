// backend/src/DataIntelligence.Core/Interfaces/IAssistantService.cs
using DataIntelligence.Core.Dtos;

namespace DataIntelligence.Core.Interfaces;

public interface IAssistantService
{
    Task<AssistantAnswerDto> AskAsync(
        int userId, AskQuestionRequest request, string? clientIp, CancellationToken cancellationToken);

    Task RecordFeedbackAsync(
        long assistantQueryId, AssistantFeedbackRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The audit log, newest first — every question the assistant was asked, including the ones it
    /// refused (NFR Auditability).
    /// </summary>
    Task<PagedResult<AssistantQueryLogDto>> GetQueryLogAsync(
        AssistantQueryLogQuery query, CancellationToken cancellationToken);

    /// <summary>One audit-log record, or null if there is no such query.</summary>
    Task<AssistantQueryLogDto?> GetQueryAsync(long assistantQueryId, CancellationToken cancellationToken);

    /// <summary>
    /// One user's past conversations, most recently used first, so they can pick one up again.
    /// </summary>
    /// <param name="limit">Ceiling on how many are returned.</param>
    Task<IReadOnlyList<AssistantSessionSummaryDto>> GetSessionsAsync(
        int userId, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// One conversation, in full, ready to be replayed into the chat view.
    /// </summary>
    /// <remarks>
    /// Takes the user id and does not merely filter on it as a convenience: a session id is a
    /// bare GUID in a URL, and without this check anyone holding one could read a conversation
    /// that is not theirs. Returns null for both "no such session" and "not yours", so the
    /// response cannot be used to discover which session ids exist.
    /// </remarks>
    Task<AssistantTranscriptDto?> GetTranscriptAsync(
        int userId, Guid sessionId, CancellationToken cancellationToken);
}