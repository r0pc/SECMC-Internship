// backend/src/DataIntelligence.Infrastructure/Ai/ResultSetFormatter.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataIntelligence.Infrastructure.Ai;

/// <summary>
/// Writes a result set for the model that has to describe it, in the smallest shape it can still
/// read.
/// </summary>
/// <remarks>
/// A row-per-object payload repeats every column name once per row, which is the whole cost of a
/// long result: twelve months of <c>{"ReferenceDate": ..., "YearOverYearPct": ...}</c> spends more
/// tokens on the two names than on the twenty-four values. Naming the columns once and sending each
/// row as a positional array says exactly the same thing — the summary prompt tells the model how to
/// read it — and the saving grows with the result rather than being a fixed trim.
/// <para>
/// A result too long to list is described instead — min, max and mean per column across every row,
/// plus the rows at each end. That one is not really an economy: a model shown the first sixty days
/// of a year of SOFR and asked how far the rate moved answers for sixty days, confidently and
/// wrongly, where a statistic computed over all of them is exact however long the series is.
/// </para>
/// <para>
/// This is only what the model is shown. The browser still receives the rows as objects, keyed by
/// column, exactly as before: the API contract is not a place to save tokens, and a UI that has to
/// zip two arrays back together to render a table has been made worse to make a prompt smaller.
/// </para>
/// </remarks>
public static class ResultSetFormatter
{
    /// <summary>How many rows from each end survive into a digest.</summary>
    /// <remarks>
    /// Enough to show what a row of this result looks like and where the series starts and ends,
    /// which is what the first and last rows are actually read for. More would be more of the same
    /// shape, and the statistics above them already say what the middle does.
    /// </remarks>
    private const int EdgeRows = 5;

    /// <param name="maxRows">
    /// The point at which a result is described rather than listed. At or under it every row is
    /// sent; over it the model gets statistics computed across all of them plus the rows at each
    /// end — see <see cref="AssistantOptions.MaxSummaryRows"/>.
    /// </param>
    public static string ToCompactJson(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, int maxRows)
    {
        if (rows.Count == 0)
        {
            // The shape is kept even when there is nothing in it. An answer that has to explain an
            // empty result reads better having been shown the columns the query asked for than an
            // undifferentiated "no rows".
            return """{"columns":[],"rows":[],"rowCount":0}""";
        }

        // Taken from the first row rather than from a union of every row's keys: a reader over one
        // result set returns the same columns in the same order for every row of it, and a
        // per-row union would spend the work to discover that.
        var columns = rows[0].Keys.ToArray();

        return rows.Count <= Math.Max(maxRows, 1)
            ? JsonSerializer.Serialize(Listed(rows, columns))
            : JsonSerializer.Serialize(Described(rows, columns));
    }

    /// <summary>A result short enough to hand over whole.</summary>
    private static CompactResultSet Listed(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string[] columns) =>
        new(columns,
            rows.Select(row => columns.Select(c => Simplify(row.GetValueOrDefault(c))).ToArray())
                .ToArray(),
            rows.Count,
            null);

    /// <summary>
    /// A long result as statistics over every row, plus the rows at each end.
    /// </summary>
    /// <remarks>
    /// This replaced sending the first N rows and calling the rest truncated, and the reason is
    /// accuracy before economy. Asked for the range of a year of SOFR, a model shown the first sixty
    /// days can only describe those sixty — the true high may sit in the seventh month it never
    /// saw — and nothing in a truncated list tells it that the figure it is about to give is wrong
    /// rather than partial. The statistics here are computed across the whole result, so the answer
    /// to "how far did it move" is exact no matter how long the series is.
    /// <para>
    /// The rows at each end stay because a summary alone cannot answer "where did it start and end",
    /// which is most of what a series is asked. What is gone is the arbitrary middle, which is the
    /// part no sentence of the answer was ever going to mention.
    /// </para>
    /// <para>
    /// The note carries its own explanation of this shape rather than the summariser's system prompt
    /// doing it. That prompt is sent on every question and most results are short, so a description
    /// of the long-result shape would be paid for overwhelmingly by answers that never see one.
    /// </para>
    /// </remarks>
    private static DescribedResultSet Described(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string[] columns)
    {
        var summary = columns.ToDictionary(
            column => column,
            column => Describe(rows.Select(r => r.GetValueOrDefault(column)).ToArray()));

        object?[][] Edge(IEnumerable<IReadOnlyDictionary<string, object?>> slice) =>
            slice.Select(row => columns.Select(c => Simplify(row.GetValueOrDefault(c))).ToArray())
                 .ToArray();

        return new DescribedResultSet(
            columns,
            rows.Count,
            summary,
            Edge(rows.Take(EdgeRows)),
            Edge(rows.Skip(Math.Max(rows.Count - EdgeRows, EdgeRows))),
            $"This result has {rows.Count} rows — too many to list, so they are summarised. "
            + "\"summary\" holds min, max and mean computed across EVERY row, so those figures are "
            + "exact for the whole result. \"first\" and \"last\" are the rows at each end in the "
            + "query's own order; the rows between them are not shown. Describe the result from "
            + "these — say how many rows there were, what range they cover and how the series "
            + "behaves. Do not present \"first\" or \"last\" as the complete result, and do not "
            + "state any figure that is not here.");
    }

    /// <summary>
    /// One column of a long result, in whatever terms its values admit.
    /// </summary>
    /// <remarks>
    /// Typed off the values rather than off the reader's schema, because what reaches here has
    /// already been boxed through <c>object</c> and a computed column ("AvgRate", "ChangeInPct")
    /// arrives as whatever SQL Server decided it was. A column whose values are all numbers gets
    /// arithmetic; one whose values are all dates gets its span; anything else is counted, because
    /// the honest summary of a text column is how many different things are in it.
    /// </remarks>
    private static ColumnSummary Describe(object?[] values)
    {
        var present = values.Where(v => v is not null).ToArray();
        var nulls = values.Length - present.Length;

        if (present.Length == 0)
        {
            return new ColumnSummary(null, null, null, null, null, nulls);
        }

        if (present.All(IsNumber))
        {
            var numbers = present.Select(Convert.ToDouble).ToArray();

            // Rounded because a mean is the one figure here that is computed rather than read, and
            // seventeen significant figures of it would be false precision that an answer might
            // quote verbatim.
            return new ColumnSummary(
                Round(numbers.Min()), Round(numbers.Max()), Round(numbers.Average()),
                null, null, nulls);
        }

        if (present.All(v => v is DateTime or DateOnly))
        {
            var dates = present.Select(ToDate).ToArray();

            return new ColumnSummary(
                null, null, null,
                dates.Min().ToString("yyyy-MM-dd"), dates.Max().ToString("yyyy-MM-dd"),
                nulls);
        }

        return new ColumnSummary(null, null, null, null, null, nulls)
        {
            Distinct = present.Select(v => v.ToString()).Distinct(StringComparer.Ordinal).Count()
        };
    }

    private static bool IsNumber(object? value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal;

    private static DateOnly ToDate(object value) => value switch
    {
        DateTime datetime => DateOnly.FromDateTime(datetime),
        DateOnly date => date,
        _ => default
    };

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.ToEven);

    /// <summary>
    /// One value, in the shortest form that does not lose anything the answer could need.
    /// </summary>
    /// <remarks>
    /// Dates are the reason this exists. <c>DateTime</c> serialises as
    /// <c>"2025-06-01T00:00:00"</c>, and a CPI month or a SOFR business day never carries a time —
    /// so nine characters per date are spent saying midnight, on every row of every result. A value
    /// that does have a time keeps it, because then it is data.
    /// </remarks>
    private static object? Simplify(object? value) => value switch
    {
        null => null,
        DateTime { TimeOfDay.Ticks: 0 } date => date.ToString("yyyy-MM-dd"),
        DateTime datetime => datetime.ToString("yyyy-MM-ddTHH:mm:ss"),
        DateOnly date => date.ToString("yyyy-MM-dd"),
        TimeSpan time => time.ToString(),
        Guid guid => guid.ToString(),
        byte[] bytes => $"<{bytes.Length} bytes>",
        _ => value
    };

    /// <param name="Columns">The field names, once, in the order every row repeats them.</param>
    /// <param name="Rows">One array per row, holding that row's values in <paramref name="Columns"/> order.</param>
    /// <param name="RowCount">How many rows the query returned, which is not always how many are here.</param>
    /// <param name="Note">
    /// Present only when the set was cut. Omitted when null rather than sent as an empty field — a
    /// note saying nothing still costs the model a line to read.
    /// </param>
    /// <remarks>
    /// Named in camelCase on the wire, matching the names the summary prompt uses for them. The
    /// serializer would otherwise write the C# spelling, and a prompt describing <c>"columns"</c>
    /// beside a payload holding <c>"Columns"</c> is a small mismatch with an outsized cost on a
    /// small model.
    /// </remarks>
    private sealed record CompactResultSet(
        [property: JsonPropertyName("columns")] string[] Columns,
        [property: JsonPropertyName("rows")] object?[][] Rows,
        [property: JsonPropertyName("rowCount")] int RowCount,
        [property: JsonPropertyName("note")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Note);

    /// <summary>A result too long to list: what it contains, and its two ends.</summary>
    /// <param name="RowCount">Every row the query returned, not the handful shown below.</param>
    /// <param name="Summary">One entry per column, computed across all <paramref name="RowCount"/> rows.</param>
    private sealed record DescribedResultSet(
        [property: JsonPropertyName("columns")] string[] Columns,
        [property: JsonPropertyName("rowCount")] int RowCount,
        [property: JsonPropertyName("summary")] IReadOnlyDictionary<string, ColumnSummary> Summary,
        [property: JsonPropertyName("first")] object?[][] First,
        [property: JsonPropertyName("last")] object?[][] Last,
        [property: JsonPropertyName("note")] string Note);

    /// <summary>
    /// What one column holds, in whichever of these terms applies to it.
    /// </summary>
    /// <remarks>
    /// Every field is omitted when null, so a numeric column costs three entries and a date column
    /// two rather than each carrying the other's empty fields. <c>nulls</c> is omitted at zero for
    /// the same reason — a column with nothing missing has nothing to say about what is missing, and
    /// on a wide result those absent zeroes are most of the block.
    /// </remarks>
    private sealed record ColumnSummary(
        [property: JsonPropertyName("min")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Min,
        [property: JsonPropertyName("max")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Max,
        [property: JsonPropertyName("mean")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Mean,
        [property: JsonPropertyName("earliest")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Earliest,
        [property: JsonPropertyName("latest")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Latest,
        [property: JsonIgnore] int Nulls)
    {
        [JsonPropertyName("nulls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int NullCount { get; } = Nulls;

        /// <summary>How many different values a column that is neither numbers nor dates holds.</summary>
        [JsonPropertyName("distinct")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Distinct { get; init; }
    }
}
