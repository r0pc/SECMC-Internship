// backend/src/DataIntelligence.Infrastructure/Ai/ChatTranscriptWriter.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;

namespace DataIntelligence.Infrastructure.Ai;

/// <summary>
/// Reads and writes the JSON document stored on <see cref="AssistantSession.TranscriptJson"/>,
/// which is the assistant's record of a conversation.
/// </summary>
/// <remarks>
/// This document is now the store, not a projection of one: nothing else records what was asked,
/// what SQL it became, or whether that SQL ran. Every write is therefore a read-modify-write of the
/// whole document — load it, change the turn in flight, serialise it back — and the cost is
/// quadratic across a session. That is affordable because a session is a chat: tens of turns, not
/// thousands.
/// <para>
/// The consequence worth knowing is concurrency. A row insert per turn could not lose a turn; a
/// document rewrite can. Two questions answered against one session at the same moment will both
/// load the same document and the later save will win, dropping the other turn entirely. A chat UI
/// asks one question at a time, so this is unlikely rather than impossible — see the note in
/// AssistantService.
/// </para>
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
    public static string Serialize(AssistantSession session, IReadOnlyList<ChatTranscriptTurn> turns)
    {
        var transcript = new ChatTranscript
        {
            SessionId = session.SessionId,
            UserId = session.UserId,
            StartedAtPkt = session.StartedAtPkt,
            LastActivityAtPkt = session.LastActivityAtPkt,
            TurnCount = turns.Count,
            Turns = turns
        };

        return JsonSerializer.Serialize(transcript, Format);
    }

    /// <summary>The turns already recorded for a session, or an empty list if there are none.</summary>
    /// <remarks>
    /// An unreadable document yields no turns rather than an exception. The alternative is a
    /// session that can never be added to again: every subsequent question would fail on the same
    /// unparseable string, and the conversation would be dead rather than merely damaged.
    /// </remarks>
    public static List<ChatTranscriptTurn> ReadTurns(string? json) =>
        Deserialize(json)?.Turns.ToList() ?? [];

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

}
