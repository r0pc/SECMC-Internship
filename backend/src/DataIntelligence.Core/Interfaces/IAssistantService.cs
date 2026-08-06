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
}