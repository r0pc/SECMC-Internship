// backend/src/DataIntelligence.Core/Entities/AssistantSession.cs
namespace DataIntelligence.Core.Entities;

/// <summary>
/// A conversation with the AI assistant, scoped to one user (FR-13).
/// </summary>
public class AssistantSession
{
    public Guid SessionId { get; set; }
    public int UserId { get; set; }
    public DateTime StartedAtPkt { get; set; }
    public DateTime LastActivityAtPkt { get; set; }

    /// <summary>
    /// The whole conversation as one JSON document — every turn, in order, as the chat was had.
    /// </summary>
    /// <remarks>
    /// This is the record. <c>ai.AssistantQuery</c> is gone, so what is not in here was not kept:
    /// each turn carries the question, the SQL it became, the bound parameters, the outcome, the
    /// answer, token usage and timings. See <c>ChatTranscriptTurn</c> for the shape and
    /// <c>AssistantTurn</c> for the OPENJSON view that reads it back.
    /// <para>
    /// Result rows are deliberately left out. A turn may return up to
    /// <c>SqlSafetyValidator.MaxRows</c> rows, and folding those into a document rewritten on every
    /// turn would grow it by megabytes to store what re-running the stored statement reproduces
    /// exactly.
    /// </para>
    /// Null until the session's first turn completes.
    /// </remarks>
    public string? TranscriptJson { get; set; }

    /// <summary>
    /// What the whole conversation has cost the model so far, in tokens — the turns' totals added
    /// up, rewritten on every save.
    /// </summary>
    /// <remarks>
    /// A stored column rather than a number derived from <see cref="TranscriptJson"/> on read. The
    /// chat list is the one screen that shows this, and it shows it for every conversation a user
    /// has: deriving it there would mean shredding each of their transcripts with OPENJSON to
    /// return one integer per chat, which is the whole history parsed to draw a list. The column is
    /// written by the code that already holds the turns in memory, and costs nothing to read.
    /// <para>
    /// The price is that it is a denormalisation: it is correct only because
    /// <c>AssistantService.SaveTurnsAsync</c> is the single writer and recomputes it from the turns
    /// on every write. Anything that edits <see cref="TranscriptJson"/> without going through that
    /// leaves this stale, and nothing in the database will notice.
    /// </para>
    /// <para>
    /// Counts the turns that reported usage. Null means no turn in the conversation reported any —
    /// "not known", which is not the same as a conversation that cost nothing, and is why this is
    /// nullable rather than defaulted to zero. A conversation where only some turns reported usage
    /// sums those, so this is a floor in that case rather than an exact figure.
    /// </para>
    /// </remarks>
    public int? TotalTokens { get; set; }
}