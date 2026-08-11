using System.Text.Json;
using DataIntelligence.Infrastructure.Ai;

namespace DataIntelligence.UnitTests.Ai;

/// <summary>
/// The shape a result set is shown to the model in, which is not the shape the browser is given.
/// </summary>
/// <remarks>
/// Everything here is about the size of a prompt. A result set is the one part of it that grows
/// with the question — the schema is fixed, the conversation is capped, and a query over a year of
/// SOFR returns two hundred and fifty rows — so it is where a per-row saving compounds. What must
/// not be traded for it is the model's ability to read the thing: a compact payload that gets
/// described wrongly costs more than the tokens it saved.
/// </remarks>
public class ResultSetFormatterTests
{
    [Fact]
    public void NamesEachColumnOnceAndSendsTheRowsPositionally()
    {
        var json = ResultSetFormatter.ToCompactJson(
            [
                Row(("ReferenceDate", "2025-06-01"), ("IndexValue", 322.561m)),
                Row(("ReferenceDate", "2025-07-01"), ("IndexValue", 323.048m))
            ],
            maxRows: 100);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(
            ["ReferenceDate", "IndexValue"],
            root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));

        // The saving, stated as a fact about the output: the column names appear once between them,
        // not once per row. Over a year of daily SOFR that is the difference between the names and
        // the values costing about the same and the names costing three times as much.
        Assert.Equal(1, Occurrences(json, "ReferenceDate"));

        var rows = root.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("2025-06-01", rows[0][0].GetString());
        Assert.Equal(322.561m, rows[0][1].GetDecimal());
    }

    [Fact]
    public void DescribesALongResultInsteadOfListingPartOfIt()
    {
        // The accuracy case, not the token one. A model shown the first ten of five hundred rows
        // and asked how far the value moved answers for ten rows — confidently, and wrongly, since
        // the true extremes are in the four hundred and ninety it never saw.
        var rows = Enumerable.Range(1, 500).Select(i => Row(("N", i))).ToList();

        var json = ResultSetFormatter.ToCompactJson(rows, maxRows: 10);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(500, root.GetProperty("rowCount").GetInt32());

        // Computed over every row, so the extremes are the real ones rather than the first ten.
        var n = root.GetProperty("summary").GetProperty("N");
        Assert.Equal(1, n.GetProperty("min").GetDouble());
        Assert.Equal(500, n.GetProperty("max").GetDouble());
        Assert.Equal(250.5, n.GetProperty("mean").GetDouble());

        // Both ends, because "where did it start and end" is most of what a series is asked.
        Assert.Equal(1, root.GetProperty("first")[0][0].GetInt32());
        Assert.Equal(500, root.GetProperty("last")[4][0].GetInt32());

        // And no "rows" key at all: an answer must not be able to mistake five rows for the result.
        Assert.False(root.TryGetProperty("rows", out _));
        Assert.Contains("500", root.GetProperty("note").GetString());
    }

    [Fact]
    public void ReportsTheSpanOfADateColumnRatherThanItsArithmetic()
    {
        // A mean of a date axis is meaningless; what a reader wants from it is the period covered.
        var rows = Enumerable.Range(0, 400)
            .Select(i => Row(("EffectiveDate", new DateTime(2025, 1, 1).AddDays(i))))
            .ToList();

        var json = ResultSetFormatter.ToCompactJson(rows, maxRows: 60);

        using var doc = JsonDocument.Parse(json);
        var column = doc.RootElement.GetProperty("summary").GetProperty("EffectiveDate");

        Assert.Equal("2025-01-01", column.GetProperty("earliest").GetString());
        Assert.Equal("2026-02-04", column.GetProperty("latest").GetString());
        Assert.False(column.TryGetProperty("mean", out _));
    }

    [Fact]
    public void CountsTheNullsInALongResultRatherThanLettingThemVanish()
    {
        // A LAG column is null on its first row, and a summary that silently skipped nulls would
        // let an answer describe a column as complete when it is not.
        var rows = Enumerable.Range(0, 100)
            .Select(i => Row(("PrevRate", i < 3 ? null : (object?)4.25)))
            .ToList();

        var json = ResultSetFormatter.ToCompactJson(rows, maxRows: 60);

        using var doc = JsonDocument.Parse(json);

        Assert.Equal(3, doc.RootElement.GetProperty("summary")
            .GetProperty("PrevRate").GetProperty("nulls").GetInt32());
    }

    [Fact]
    public void StillListsAResultThatFitsUnderTheThreshold()
    {
        // The common case is unchanged: a question answered with twelve months of figures gets all
        // twelve, and nothing about it is summarised or caveated.
        var rows = Enumerable.Range(1, 12).Select(i => Row(("N", i))).ToList();

        var json = ResultSetFormatter.ToCompactJson(rows, maxRows: 60);

        using var doc = JsonDocument.Parse(json);

        Assert.Equal(12, doc.RootElement.GetProperty("rows").GetArrayLength());
        Assert.False(doc.RootElement.TryGetProperty("summary", out _));
        Assert.False(doc.RootElement.TryGetProperty("note", out _));
    }

    [Fact]
    public void SaysNothingAboutSummarisingWhenNothingWasSummarised()
    {
        // An unconditional note costs a line on every answer and invites a caveat about
        // completeness into results that are complete. It also carries the explanation of the
        // summarised shape, which most questions never produce and none should pay for.
        var json = ResultSetFormatter.ToCompactJson([Row(("N", 1))], maxRows: 100);

        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("note", out _));
        Assert.Equal(1, doc.RootElement.GetProperty("rowCount").GetInt32());
    }

    [Fact]
    public void KeepsTheShapeWhenThereAreNoRowsAtAll()
    {
        // An empty result is the case the summariser has the most to explain, so it must still be
        // handed something it can recognise as a result set rather than an empty string.
        var json = ResultSetFormatter.ToCompactJson([], maxRows: 100);

        using var doc = JsonDocument.Parse(json);

        Assert.Equal(0, doc.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal(0, doc.RootElement.GetProperty("rowCount").GetInt32());
    }

    [Fact]
    public void WritesADateWithNoTimeAsJustTheDate()
    {
        // Both datasets are date-grained: a CPI month and a SOFR business day never carry a time,
        // and the default serialisation spends nine characters a row saying midnight.
        var json = ResultSetFormatter.ToCompactJson(
            [Row(("EffectiveDate", new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Unspecified)))],
            maxRows: 100);

        Assert.Contains("\"2025-06-02\"", json);
        Assert.DoesNotContain("00:00:00", json);
    }

    [Fact]
    public void KeepsATimeThatIsActuallyThere()
    {
        // Trimming midnight is dropping padding; trimming a real time would be dropping data.
        var json = ResultSetFormatter.ToCompactJson(
            [Row(("RanAt", new DateTime(2025, 6, 2, 14, 30, 0, DateTimeKind.Unspecified)))],
            maxRows: 100);

        Assert.Contains("2025-06-02T14:30:00", json);
    }

    [Fact]
    public void WritesANullAsANull()
    {
        // A LAG over the first period in range has no previous value, and "no figure here" is
        // something the answer has to be able to say.
        var json = ResultSetFormatter.ToCompactJson(
            [Row(("PrevAvgRate", null))], maxRows: 100);

        using var doc = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("rows")[0][0].ValueKind);
    }

    private static IReadOnlyDictionary<string, object?> Row(params (string Column, object? Value)[] cells) =>
        cells.ToDictionary(c => c.Column, c => c.Value);

    private static int Occurrences(string text, string value)
    {
        var count = 0;

        for (var i = text.IndexOf(value, StringComparison.Ordinal);
             i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
