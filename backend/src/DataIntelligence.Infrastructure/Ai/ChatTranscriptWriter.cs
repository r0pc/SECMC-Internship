// backend/src/DataIntelligence.Infrastructure/Ai/ChatTranscriptWriter.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;

namespace DataIntelligence.Infrastructure.Ai;

/// <summary>
/// Projects a conversation's <c>ai.AssistantQuery</c> rows into the JSON document stored on
/// <see cref="AssistantSession.TranscriptJson"/>.
/// </summary>
/// <remarks>
/// Rebuilt whole on every turn rather than appended to. Appending is cheaper and is the wrong
/// trade here: a transcript assembled incrementally drifts the moment one write is missed, and the
/// drift is invisible — the document stays valid JSON and simply stops matching the rows it claims
/// to describe. Rebuilding makes the rows the only source of truth by construction, so the two
/// cannot disagree. The cost is quadratic across a session, which is affordable precisely because
/// a session is a chat: tens of turns, not thousands.
/// </remarks>
public static class ChatTranscriptWriter
{
    /// <summary>
    /// How the stored document is shaped. Pinned here, deliberately not taken from the API's
    /// serializer settings.
    /// </summary>
    /// <remarks>
    /// The API's options describe how this service talks to a browser today, and are free to change
    /// when that conversation changes. These describe documents already sitting in a table, which
    /// are not free to change at all — sharing one set of options would let a naming-policy tweak
    /// made for a frontend silently split the transcript history into "before" and "after".
    /// </remarks>
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // Enums as names, not ordinals. A stored 4 becomes a different outcome the day a member is
        // inserted above it, and every transcript written before that day quietly changes meaning.
        Converters = { new JsonStringEnumConverter() },

        // Written to be read by a person diffing two versions of a conversation, and gzip makes
        // the whitespace close to free on the wire.
        WriteIndented = true
    };

    /// <summary>Serialises the whole conversation, oldest turn first.</summary>
    public static string Serialize(AssistantSession session, IReadOnlyList<AssistantQuery> turns)
    {
        var transcript = new ChatTranscript
        {
            SessionId = session.SessionId,
            UserId = session.UserId,
            StartedAtUtc = session.StartedAtUtc,
            LastActivityAtUtc = session.LastActivityAtUtc,
            TurnCount = turns.Count,
            Turns = turns.Select(ToTurn).ToList()
        };

        return JsonSerializer.Serialize(transcript, Format);
    }

    /// <summary>Reads a transcript back. Returns null for anything that will not parse.</summary>
    /// <remarks>
    /// Tolerant on read and strict on write, which is the right way round for a column that
    /// outlives its writer. A document written by an older version may be missing a field added
    /// since; that is a transcript to render as best it can, not an error to raise at whoever
    /// happens to open it.
    /// </remarks>
    public static ChatTranscript? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ChatTranscript>(json, Format);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ChatTranscriptTurn ToTurn(AssistantQuery q) => new()
    {
        AssistantQueryId = q.AssistantQueryId,
        AskedAtUtc = q.AskedAtUtc,
        Question = q.QuestionText,
        Answer = q.AnswerText,
        Outcome = q.ValidationOutcome,

        // Carried for every turn, including refused ones — unlike the answer DTO, which hides the
        // SQL of a rejected query. A transcript is read to understand what happened, and for a
        // refusal the statement that was turned away is the part that explains it.
        Sql = q.GeneratedSql,
        Parameters = ReadParameters(q.SqlParametersJson),
        Explanation = q.Explanation,
        ResultRowCount = q.ResultRowCount
    };

    /// <summary>
    /// Re-reads the stored parameter bag so it nests as an object in the transcript rather than
    /// being embedded as a string of JSON inside JSON.
    /// </summary>
    private static IReadOnlyDictionary<string, object?>? ReadParameters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
