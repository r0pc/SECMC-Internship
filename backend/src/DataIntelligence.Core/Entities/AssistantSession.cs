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

    /// <summary>
    /// The whole conversation as one JSON document — every turn, in order, as the chat was had.
    /// </summary>
    /// <remarks>
    /// A projection of <see cref="Queries"/>, not a second copy of the truth. The rows remain the
    /// record: they carry the CHECK constraints, the review queue's filtered index, and the
    /// guarantee that a question is logged before anything can fail. This column is rebuilt from
    /// them at the end of every turn, so the two cannot drift — and if the rebuild is what fails,
    /// what is lost is the projection, never the audit trail.
    /// <para>
    /// Result rows are deliberately left out. A turn may return up to
    /// <c>SqlSafetyValidator.MaxRows</c> rows, and folding those into a document rewritten on every
    /// turn would grow it by megabytes to store what re-running the stored statement reproduces
    /// exactly.
    /// </para>
    /// Null until the session's first turn completes.
    /// </remarks>
    public string? TranscriptJson { get; set; }

    public ICollection<AssistantQuery> Queries { get; set; } = [];
}