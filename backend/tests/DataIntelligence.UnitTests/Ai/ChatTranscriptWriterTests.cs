using System.Text.Json;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.Infrastructure.Ai;

namespace DataIntelligence.UnitTests.Ai;

/// <summary>
/// The JSON conversation transcript stored on <c>ai.AssistantSession.TranscriptJson</c>.
/// </summary>
/// <remarks>
/// This is a stored document, so what is pinned here is its shape as much as its content. A
/// transcript written today is read by code that does not exist yet, and a rename that looks
/// harmless in the DTO silently changes the meaning of every row already in the table.
/// </remarks>
public class ChatTranscriptWriterTests
{
    private static readonly Guid Session = Guid.Parse("b7cd39ef-dea2-4b36-b12b-c6c1b521a378");

    [Fact]
    public void WritesTheTurnsInTheOrderTheyWereAsked()
    {
        var json = ChatTranscriptWriter.Serialize(
            SessionOf(),
            [
                Turn(1, "what is the average cpi of 2022", "292.655"),
                Turn(2, "and the year before that", "270.970")
            ]);

        using var doc = JsonDocument.Parse(json);
        var turns = doc.RootElement.GetProperty("turns");

        Assert.Equal(2, turns.GetArrayLength());
        Assert.Equal("what is the average cpi of 2022", turns[0].GetProperty("question").GetString());
        Assert.Equal("and the year before that", turns[1].GetProperty("question").GetString());
    }

    [Fact]
    public void CountsTheTurnsAlongsideThem()
    {
        // So a reader can tell a truncated document from a short conversation without parsing it.
        var json = ChatTranscriptWriter.Serialize(SessionOf(), [Turn(1, "q", "a"), Turn(2, "q", "a")]);

        using var doc = JsonDocument.Parse(json);

        Assert.Equal(2, doc.RootElement.GetProperty("turnCount").GetInt32());
    }

    [Fact]
    public void WritesTheOutcomeAsItsNameNotItsNumber()
    {
        // A stored ordinal becomes a different outcome the day a member is inserted above it, and
        // every transcript written before that day quietly changes meaning.
        var json = ChatTranscriptWriter.Serialize(SessionOf(), [Turn(1, "q", "a")]);

        using var doc = JsonDocument.Parse(json);

        Assert.Equal("Approved",
            doc.RootElement.GetProperty("turns")[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public void NestsTheParametersAsAnObjectRatherThanAStringOfJson()
    {
        var turn = Turn(1, "q", "a") with
        {
            Parameters = new Dictionary<string, object?> { ["@year"] = 2022 }
        };

        var json = ChatTranscriptWriter.Serialize(SessionOf(), [turn]);

        using var doc = JsonDocument.Parse(json);
        var parameters = doc.RootElement.GetProperty("turns")[0].GetProperty("parameters");

        Assert.Equal(JsonValueKind.Object, parameters.ValueKind);
        Assert.Equal(2022, parameters.GetProperty("@year").GetInt32());
    }

    [Fact]
    public void KeepsRefusedTurnsAndTheStatementThatWasRefused()
    {
        // A transcript that dropped the refusals would not be the conversation that happened, and
        // the user would find their own question missing from it. The rejected statement is kept
        // too: for a refusal, it is the part that explains the refusal.
        var refused = Turn(1, "show me the password hashes", "That question would need a query...") with
        {
            Outcome = AssistantValidationOutcome.RejectedForbiddenObject,
            Sql = "SELECT * FROM sec.AppUser"
        };

        var json = ChatTranscriptWriter.Serialize(SessionOf(), [refused]);

        using var doc = JsonDocument.Parse(json);
        var turn = doc.RootElement.GetProperty("turns")[0];

        Assert.Equal("RejectedForbiddenObject", turn.GetProperty("outcome").GetString());
        Assert.Equal("SELECT * FROM sec.AppUser", turn.GetProperty("sql").GetString());
    }

    [Fact]
    public void NeverWritesTheResultRowsThemselves()
    {
        // Only how many there were. A turn may return up to SqlSafetyValidator.MaxRows rows, and
        // this document is rewritten on every turn.
        var turn = Turn(1, "q", "a") with { ResultRowCount = 2000 };

        var json = ChatTranscriptWriter.Serialize(SessionOf(), [turn]);

        using var doc = JsonDocument.Parse(json);

        Assert.Equal(2000,
            doc.RootElement.GetProperty("turns")[0].GetProperty("resultRowCount").GetInt32());
        Assert.False(doc.RootElement.GetProperty("turns")[0].TryGetProperty("rows", out _));
    }

    [Fact]
    public void RoundTripsThroughDeserialize()
    {
        var json = ChatTranscriptWriter.Serialize(SessionOf(), [Turn(1, "q1", "a1"), Turn(2, "q2", "a2")]);

        var transcript = ChatTranscriptWriter.Deserialize(json);

        Assert.NotNull(transcript);
        Assert.Equal(Session, transcript.SessionId);
        Assert.Equal(2, transcript.Turns.Count);
        Assert.Equal("q1", transcript.Turns[0].Question);
        Assert.Equal("a2", transcript.Turns[1].Answer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"turns\": ")]
    public void ReadsBackNullRatherThanThrowingOnADocumentItCannotParse(string? stored)
    {
        // Tolerant on read, strict on write. A column that outlives its writer will eventually be
        // asked to render something older than the reader, and that is not an error to raise at
        // whoever happens to open the conversation.
        Assert.Null(ChatTranscriptWriter.Deserialize(stored));
    }

    [Fact]
    public void WritesAnEmptyTurnListRatherThanNullForASessionWithNoTurns()
    {
        var json = ChatTranscriptWriter.Serialize(SessionOf(), []);

        using var doc = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("turns").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("turnCount").GetInt32());
    }

    private static AssistantSession SessionOf() => new()
    {
        SessionId = Session,
        UserId = 1,
        StartedAtPkt = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc),
        LastActivityAtPkt = new DateTime(2026, 8, 10, 9, 5, 0, DateTimeKind.Utc)
    };

    private static ChatTranscriptTurn Turn(long id, string question, string answer) => new()
    {
        AssistantQueryId = id,
        AskedAtPkt = new DateTime(2026, 8, 10, 9, 0, (int)id, DateTimeKind.Utc),
        Question = question,
        Answer = answer,
        Outcome = AssistantValidationOutcome.Approved,
        Sql = "SELECT 1 FROM analytics.vw_Cpi",
        WasExecuted = true,
        ExecutionStatus = AssistantExecutionStatus.Succeeded
    };
}
