// backend/src/DataIntelligence.Core/Entities/AssistantSession.cs
namespace DataIntelligence.Core.Entities;

/// <summary>
/// A conversation with the AI assistant, scoped to one user (FR-13).
/// </summary>
public class AssistantSession
{
    public Guid SessionId { get; set; }
    public int UserId { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime LastActivityAtUtc { get; set; }

    public ICollection<AssistantQuery> Queries { get; set; } = [];
}