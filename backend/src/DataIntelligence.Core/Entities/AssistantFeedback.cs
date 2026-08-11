// backend/src/DataIntelligence.Core/Entities/AssistantFeedback.cs
namespace DataIntelligence.Core.Entities;

/// <summary>Thumbs up/down on one answer, for tuning the prompt and the validator over time.</summary>
public class AssistantFeedback
{
    public long AssistantQueryId { get; set; }
    public bool IsHelpful { get; set; }
    public string? Comment { get; set; }
    public DateTime SubmittedAtPkt { get; set; }

}