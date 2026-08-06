// backend/src/DataIntelligence.Core/Interfaces/IAssistantService.cs
using DataIntelligence.Core.Dtos;

namespace DataIntelligence.Core.Interfaces;

public interface IAssistantService
{
    Task<AssistantAnswerDto> AskAsync(
        int userId, AskQuestionRequest request, string? clientIp, CancellationToken cancellationToken);

    Task RecordFeedbackAsync(
        long assistantQueryId, AssistantFeedbackRequest request, CancellationToken cancellationToken);
}