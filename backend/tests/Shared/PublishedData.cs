using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DataIntelligence.TestSupport;

/// <summary>
/// The sample extracts in <c>docs/example_data/</c>, read as the reference dataset, plus the
/// publisher payloads that carry the same figures.
/// </summary>
/// <remarks>
/// The point of these is accuracy at full published scale. A hand-written fixture proves the
/// adapter handles the shapes someone thought to write down; the real extracts contain 1,500-odd
/// CPI figures spanning 1913 to date, a month the publisher has not filled in yet, values at one
/// and three decimal places, and four out-of-scope rates interleaved with SOFR on every business
/// day. Those are the cases that actually occur.
/// <para>
/// What this does <b>not</b> prove: that the JSON field names match the live APIs. The extracts
/// are CSV downloads, so the payload builders below reproduce each publisher's JSON shape from
/// them — the figures are real, the envelope is reconstructed. Field names are verified by
/// running against the live endpoints, not here.
/// </para>
/// <para>
/// Shared by both test projects through a linked compile item rather than a third project: it is
/// one file of test support, and a project would cost more to carry than it saves.
/// </para>
/// </remarks>
public static class PublishedData
{
    /// <summary>The CSV column order for CPI, mapped to the BLS period token each column is.</summary>
    private static readonly (string Column, string PeriodCode)[] CpiColumns =
    [
        ("Jan", "M01"), ("Feb", "M02"), ("Mar", "M03"), ("Apr", "M04"),
        ("May", "M05"), ("Jun", "M06"), ("Jul", "M07"), ("Aug", "M08"),
        ("Sep", "M09"), ("Oct", "M10"), ("Nov", "M11"), ("Dec", "M12"),
        ("Annual", "M13"), ("HALF1", "S01"), ("HALF2", "S02")
    ];

    private static readonly Lazy<IReadOnlyList<CpiCell>> LazyCpi = new(ReadCpi);
    private static readonly Lazy<IReadOnlyList<SofrCsvRow>> LazySofr = new(ReadSofr);

    /// <summary>Every populated cell of CPI.csv, in publication order.</summary>
    public static IReadOnlyList<CpiCell> CpiCells => LazyCpi.Value;

    /// <summary>Every data row of SOFR.csv, all five rate types.</summary>
    public static IReadOnlyList<SofrCsvRow> SofrRows => LazySofr.Value;

    /// <summary>The rows in scope: rate type SOFR, newest first as published.</summary>
    public static IReadOnlyList<SofrCsvRow> SofrOnly =>
        SofrRows.Where(r => r.RateType == "SOFR").ToList();

    /// <summary>The rows the adapter must reject: the four other rates sharing the payload.</summary>
    public static IReadOnlyList<SofrCsvRow> OtherRates =>
        SofrRows.Where(r => r.RateType != "SOFR").ToList();

    /// <summary>
    /// A BLS v2 response carrying the CPI extract: a success envelope, one series, and values as
    /// strings with an empty footnote array — the shape api.bls.gov actually returns.
    /// </summary>
    public static string BlsPayload(IEnumerable<CpiCell>? cells = null)
    {
        var buffer = new MemoryStream();

        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("status", "REQUEST_SUCCEEDED");
            json.WriteNumber("responseTime", 67);

            json.WriteStartObject("Results");
            json.WriteStartArray("series");
            json.WriteStartObject();
            json.WriteString("seriesID", "CUUR0000SA0");
            json.WriteStartArray("data");

            foreach (var cell in cells ?? CpiCells)
            {
                json.WriteStartObject();
                json.WriteString("year", cell.Year.ToString(CultureInfo.InvariantCulture));
                json.WriteString("period", cell.PeriodCode);
                json.WriteString("value", cell.Text);

                // BLS emits [{}] when a figure carries no footnote, which is not the same as an
                // empty array and has tripped up parsers before.
                json.WriteStartArray("footnotes");
                json.WriteStartObject();

                if (cell.Footnote is { } footnote)
                {
                    json.WriteString("code", footnote);
                }

                json.WriteEndObject();
                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// A NY Fed reference-rates response carrying the SOFR extract. Includes the other four rates
    /// by default, because they arrive in the real payload and the adapter's job is to leave them
    /// out of the table.
    /// </summary>
    public static string SofrPayload(IEnumerable<SofrCsvRow>? rows = null)
    {
        var buffer = new MemoryStream();

        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartArray("refRates");

            foreach (var row in rows ?? SofrRows)
            {
                json.WriteStartObject();
                json.WriteString("effectiveDate",
                    row.EffectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                json.WriteString("type", row.RateType);
                json.WriteNumber("percentRate", row.Rate);

                WriteOptional(json, "percentPercentile1", row.Percentile1);
                WriteOptional(json, "percentPercentile25", row.Percentile25);
                WriteOptional(json, "percentPercentile75", row.Percentile75);
                WriteOptional(json, "percentPercentile99", row.Percentile99);
                WriteOptional(json, "volumeInBillions", row.Volume);

                // Empty rather than absent when a day has not been revised, exactly as the
                // published file has it.
                json.WriteString("revisionIndicator", row.RevisionIndicator ?? string.Empty);

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());

        static void WriteOptional(Utf8JsonWriter json, string name, decimal? value)
        {
            if (value is { } present)
            {
                json.WriteNumber(name, present);
            }
        }
    }

    // ------------------------------------------------------------------ reading

    private static IReadOnlyList<CpiCell> ReadCpi()
    {
        var lines = File.ReadAllLines(PathTo("CPI.csv"));

        // The file opens with ten lines of series metadata before the column header. Finding the
        // header rather than skipping a fixed count means a publisher adding a line does not
        // silently shift every value by one column.
        var headerIndex = Array.FindIndex(lines, l => l.StartsWith("Year,", StringComparison.Ordinal));

        if (headerIndex < 0)
        {
            throw new InvalidOperationException("CPI.csv has no 'Year' header row.");
        }

        var header = SplitCsvLine(lines[headerIndex]);
        var columnIndex = CpiColumns.ToDictionary(
            c => c.PeriodCode,
            c => Array.IndexOf(header, c.Column));

        foreach (var (periodCode, index) in columnIndex)
        {
            if (index < 0)
            {
                throw new InvalidOperationException($"CPI.csv has no column for period {periodCode}.");
            }
        }

        var cells = new List<CpiCell>();

        foreach (var line in lines.Skip(headerIndex + 1))
        {
            var fields = SplitCsvLine(line);

            if (fields.Length == 0
                || !short.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year))
            {
                continue;
            }

            foreach (var (_, periodCode) in CpiColumns)
            {
                var index = columnIndex[periodCode];
                var text = index < fields.Length ? fields[index].Trim() : string.Empty;

                // A blank cell is a figure the publisher has not released — October 2025 at the
                // time of writing. Absent, not zero, and the collector must write no row for it.
                if (text.Length == 0)
                {
                    continue;
                }

                cells.Add(new CpiCell(
                    year,
                    periodCode,
                    decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture),
                    text,
                    null));
            }
        }

        return cells;
    }

    private static IReadOnlyList<SofrCsvRow> ReadSofr()
    {
        var lines = File.ReadAllLines(PathTo("SOFR.csv"));
        var header = SplitCsvLine(lines[0]);

        var rows = new List<SofrCsvRow>();

        foreach (var line in lines.Skip(1))
        {
            var fields = SplitCsvLine(line);

            if (fields.Length == 0 || fields[0].Trim().Length == 0)
            {
                continue;
            }

            var date = DateOnly.ParseExact(fields[0].Trim(), "MM/dd/yyyy", CultureInfo.InvariantCulture);
            var revision = Field(header, fields, "Revision Indicator (Y/N)");

            rows.Add(new SofrCsvRow(
                date,
                Field(header, fields, "Rate Type")!,
                Number(header, fields, "Rate (%)")!.Value,
                Number(header, fields, "1st Percentile (%)"),
                Number(header, fields, "25th Percentile (%)"),
                Number(header, fields, "75th Percentile (%)"),
                Number(header, fields, "99th Percentile (%)"),
                Number(header, fields, "Volume ($Billions)"),
                revision));
        }

        return rows;
    }

    private static string? Field(string[] header, string[] fields, string column)
    {
        var index = Array.IndexOf(header, column);

        if (index < 0)
        {
            throw new InvalidOperationException($"SOFR.csv has no '{column}' column.");
        }

        var value = index < fields.Length ? fields[index].Trim() : string.Empty;

        return value.Length == 0 ? null : value;
    }

    private static decimal? Number(string[] header, string[] fields, string column) =>
        Field(header, fields, column) is { } text
            ? decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// Splits one CSV line, honouring double-quoted fields. The data rows of both extracts are
    /// plain, but CPI.csv's metadata header quotes a series title containing commas, and a naive
    /// split would misread it into the column search above.
    /// </summary>
    private static string[] SplitCsvLine(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // A doubled quote inside a quoted field is one literal quote.
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());

        return fields.Count == 1 && fields[0].Length == 0 ? [] : [.. fields];
    }

    private static string PathTo(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ExampleData", fileName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"{fileName} was not copied to the test output. Check the ExampleData item group "
                + "in the test project.", path);
        }

        return path;
    }
}

/// <summary>
/// One populated cell of CPI.csv: a year, the BLS period token for the column it came from, and
/// the figure.
/// </summary>
/// <param name="Text">
/// The value exactly as published, before parsing. Kept so a payload can carry the publisher's
/// own formatting — one decimal place before 2007, three after — rather than .NET's rendering
/// of the parsed number.
/// </param>
/// <param name="Footnote">Not present in the extract; set by tests that need a revision marker.</param>
public sealed record CpiCell(short Year, string PeriodCode, decimal Value, string Text, string? Footnote)
{
    /// <summary>First day of the period, derived independently of the code under test.</summary>
    public DateOnly ReferenceDate => PeriodCode switch
    {
        "M13" or "S01" => new DateOnly(Year, 1, 1),
        "S02" => new DateOnly(Year, 7, 1),
        _ => new DateOnly(Year, int.Parse(PeriodCode[1..], CultureInfo.InvariantCulture), 1)
    };
}

/// <summary>One data row of SOFR.csv, whichever rate it is for.</summary>
public sealed record SofrCsvRow(
    DateOnly EffectiveDate,
    string RateType,
    decimal Rate,
    decimal? Percentile1,
    decimal? Percentile25,
    decimal? Percentile75,
    decimal? Percentile99,
    decimal? Volume,
    string? RevisionIndicator);
