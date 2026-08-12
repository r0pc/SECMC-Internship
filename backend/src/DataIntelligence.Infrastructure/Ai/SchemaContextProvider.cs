// backend/src/DataIntelligence.Infrastructure/Ai/SchemaContextProvider.cs
using DataIntelligence.Core;
using System.Text;
using DataIntelligence.Core.Exceptions;
using DataIntelligence.Core.Interfaces;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace DataIntelligence.Infrastructure.Ai;

/// <inheritdoc cref="ISchemaContextProvider"/>
/// <remarks>
/// The context has two halves, kept apart on purpose.
/// <para>
/// **Structure** — which views exist and what columns they have — is read from
/// <c>INFORMATION_SCHEMA</c>, filtered to <see cref="SqlSafetyValidator.AllowedObjects"/>. It
/// therefore cannot drift from the database: a column added, renamed or dropped changes what the
/// model is told on the next start, and a view the validator would refuse is never described in the
/// first place.
/// </para>
/// <para>
/// **Meaning** — that <c>M13</c> is an annual average, that volume is in billions, that the dialect
/// is T-SQL — stays hand-written below. None of it is derivable from column metadata, and all of it
/// was added because the model got something wrong without it. Generating the prose would lose the
/// part that made the difference.
/// </para>
/// Cached for the process lifetime: the schema does not change under a running API, and rebuilding
/// it per question would add a round trip to every call.
/// </remarks>
public sealed class SchemaContextProvider : ISchemaContextProvider
{
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _structure;

    /// <summary>How long a coverage reading is reused before it is read again.</summary>
    /// <remarks>
    /// Short, because this is not really a cache of anything slow — it is there so that one question
    /// costs one reading. The coverage window goes into the prompt on every question, and again into
    /// the summary prompt when a result comes back empty, and those two used to be separate round
    /// trips for an answer that cannot have changed between them.
    /// <para>
    /// A minute is chosen to be shorter than anything that could matter. Coverage moves when the
    /// collector writes a new period, which is a daily event at most, so a reading a few seconds old
    /// is the same reading — while a longer window would start to mean the assistant describing
    /// yesterday's holdings after this morning's collection, for no gain over a minute.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan CoverageFreshness = TimeSpan.FromMinutes(1);

    private IReadOnlyList<(string Label, DateOnly Earliest, DateOnly Latest)>? _coverage;
    private DateTimeOffset _coverageReadAt;

    public SchemaContextProvider(IConfiguration configuration, TimeProvider timeProvider)
    {
        _connectionString = configuration.GetConnectionString(DependencyInjection.ConnectionStringName)
            ?? string.Empty;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// What each view is for, in the terms a question will be asked in. Keyed by view name.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Notes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["vw_Cpi"] = "Current CPI figures, one row per period.",
            ["vw_CpiMonthlyChange"] = "Monthly CPI with month-over-month and year-over-year % change.",
            ["vw_CpiAnnual"] = "The annual and semiannual averages BLS publishes, one row per year.",
            ["vw_Sofr"] = "Current SOFR daily rates. VolumeUsdBillions is in billions of dollars, not dollars.",
            ["vw_SofrAnnual"] = "SOFR summarised per calendar year.",
            ["vw_CpiRevision"] = "Every vintage of a CPI period that has ever been revised.",
            ["vw_SofrRevision"] = "Every vintage of a SOFR date that has ever been revised."
        };

    public async Task<string> GetContextAsync(CancellationToken cancellationToken)
    {
        var structure = await GetStructureAsync(cancellationToken);
        var temporal = await BuildTemporalAsync(cancellationToken);

        // Structure is cached; "now" and the coverage window are not. A process that has been up
        // since yesterday would otherwise answer "last month" against yesterday's idea of the
        // calendar, and would keep claiming the latest figure is whatever it was at startup.
        return structure + temporal;
    }

    public async Task<string> GetCoverageAsync(CancellationToken cancellationToken)
    {
        var coverage = await ReadCoverageAsync(cancellationToken);

        if (coverage.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("What this platform holds:");

        foreach (var (label, earliest, latest) in coverage)
        {
            builder.Append("- ").Append(label).Append(": ")
                .Append(earliest.ToString("yyyy-MM-dd")).Append(" to ")
                .Append(latest.ToString("yyyy-MM-dd")).AppendLine();
        }

        return builder.ToString();
    }

    private async Task<string> GetStructureAsync(CancellationToken cancellationToken)
    {
        if (_structure is not null)
        {
            return _structure;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Re-checked inside the gate: several questions arriving together would otherwise each
            // build it, and the last one would win after the others had already paid for the trip.
            _structure ??= await BuildAsync(cancellationToken);
            return _structure;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Today's date and what the database actually holds, rebuilt per question.
    /// </summary>
    /// <remarks>
    /// Without this the model has no notion of "now", so *"the average SOFR rate last month"* is
    /// unanswerable — not because the query is hard, but because "last month" cannot be resolved.
    /// It correctly refuses rather than guessing a year, which is the right failure and a useless
    /// answer. Telling it the date turns the whole class of relative-date questions into ordinary
    /// ones.
    /// <para>
    /// The coverage window is here for a second reason: the newest CPI figure is typically a few
    /// weeks behind today, so "last month" and "the most recent month with data" are often
    /// different months. A model told only the date would confidently produce an empty result.
    /// </para>
    /// One small query per question, against views that are already indexed on their date axis —
    /// next to a multi-second model call, it does not register.
    /// </remarks>
    private async Task<string> BuildTemporalAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(PakistanTime.Now(_timeProvider));

        var coverage = await ReadCoverageAsync(cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine();
        builder.Append("Today's date is ").Append(today.ToString("yyyy-MM-dd")).AppendLine(" (Pakistan Standard Time, UTC+05:00).");
        builder.AppendLine(
            "Resolve every relative date against it — \"last month\", \"this year\", \"the last 6 "
            + "months\", \"year to date\" — and write the resulting dates in as parameters; never "
            + "ask which dates were meant. Weeks are rolling, not calendar: \"this week\"/\"the "
            + "past week\" = the 7 days ending today inclusive, \"last week\" = the 7 before "
            + "those, \"the last N weeks\" = N*7 days ending today.");

        if (coverage.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("What the database currently holds:");

            foreach (var (label, earliest, latest) in coverage)
            {
                builder.Append("- ").Append(label).Append(": ")
                    .Append(earliest.ToString("yyyy-MM-dd")).Append(" to ")
                    .Append(latest.ToString("yyyy-MM-dd")).AppendLine();
            }

            builder.AppendLine(
                "Publishers release in arrears, so the newest figure is normally older than today. "
                + "If a range falls partly or wholly outside what is held, still query the range "
                + "asked for — a short or empty result is the honest answer.");
        }

        return builder.ToString();
    }

    /// <summary>The dataset date axes worth reporting a range for.</summary>
    private static readonly (string Label, string View, string Column)[] DateAxes =
    [
        ("CPI (analytics.vw_Cpi, ReferenceDate)", "analytics.vw_Cpi", "ReferenceDate"),
        ("SOFR (analytics.vw_Sofr, EffectiveDate)", "analytics.vw_Sofr", "EffectiveDate"),
    ];

    /// <summary>
    /// The first and last date held per dataset, from the last reading if it is still fresh.
    /// </summary>
    /// <remarks>
    /// One query per dataset rather than a single UNION, so a view that is missing costs its own
    /// line and not the whole block. A partially deployed database should still be able to tell
    /// the model what it does have.
    /// <para>
    /// Unsynchronised, unlike the structure cache above. Two questions arriving together may both
    /// read, and the loser's reading is discarded — which costs one round trip and cannot produce a
    /// wrong answer, since both read the same rows. A lock here would serialise every question
    /// behind a query that takes milliseconds to save a duplicate of itself. The reference is
    /// published after the timestamp is read and swapped whole, so a reader sees either the previous
    /// list or the new one, never a half-built one.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<(string Label, DateOnly Earliest, DateOnly Latest)>>
        ReadCoverageAsync(CancellationToken cancellationToken)
    {
        if (_coverage is { } cached
            && _timeProvider.GetUtcNow() - _coverageReadAt < CoverageFreshness)
        {
            return cached;
        }

        var coverage = new List<(string, DateOnly, DateOnly)>();

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var (label, view, column) in DateAxes)
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT MIN({column}), MAX({column}) FROM {view}";

                await using var reader = await command.ExecuteReaderAsync(cancellationToken);

                if (!await reader.ReadAsync(cancellationToken)
                    || reader.IsDBNull(0)
                    || reader.IsDBNull(1))
                {
                    continue; // Nothing collected for that dataset yet — say nothing about it.
                }

                coverage.Add((
                    label,
                    DateOnly.FromDateTime(reader.GetDateTime(0)),
                    DateOnly.FromDateTime(reader.GetDateTime(1))));
            }
            catch (SqlException)
            {
                // Coverage is an aid, not a prerequisite. A missing view should cost the hint, not
                // the question — the structural half of the context is what the model needs to
                // write SQL at all.
            }
        }

        _coverageReadAt = _timeProvider.GetUtcNow();
        _coverage = coverage;

        return coverage;
    }

    private async Task<string> BuildAsync(CancellationToken cancellationToken)
    {
        var columns = await ReadColumnsAsync(cancellationToken);

        if (columns.Count == 0)
        {
            throw new AssistantNotConfiguredException(
                "None of the analytics views the assistant reads exist in this database. They are "
                + "created by section 5 of docs/database-schema.sql, which has not been run here.");
        }

        var builder = new StringBuilder();
        builder.AppendLine("You may query ONLY the following SQL Server views. Every column name is exact.");
        builder.AppendLine();

        foreach (var (view, viewColumns) in columns.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append("analytics.").Append(view).Append('(');
            builder.Append(string.Join(", ", viewColumns));
            builder.AppendLine(")");

            if (Notes.TryGetValue(view, out var note))
            {
                builder.Append("    -- ").AppendLine(note);
            }
        }

        builder.AppendLine();
        builder.Append(Semantics);

        return builder.ToString();
    }

    /// <summary>
    /// Reads the columns of every allowed view, in declaration order.
    /// </summary>
    /// <remarks>
    /// Filtered to the allow-list in code rather than by schema alone: a view added to
    /// <c>analytics</c> without being added to the validator would otherwise be described to the
    /// model and then refused when it used it, which reads to the user as the assistant being
    /// broken rather than as the view being off-limits.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, List<string>>> ReadColumnsAsync(
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new AssistantNotConfiguredException(
                $"Connection string '{DependencyInjection.ConnectionStringName}' is not configured, "
                + "so the schema shown to the model cannot be read.");
        }

        var allowedViews = SqlSafetyValidator.AllowedObjects
            .Select(o => o[(o.IndexOf('.') + 1)..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT  TABLE_NAME, COLUMN_NAME
            FROM    INFORMATION_SCHEMA.COLUMNS
            WHERE   TABLE_SCHEMA = 'analytics'
            ORDER BY TABLE_NAME, ORDINAL_POSITION
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var view = reader.GetString(0);
            if (!allowedViews.Contains(view))
            {
                continue;
            }

            if (!result.TryGetValue(view, out var list))
            {
                list = [];
                result[view] = list;
            }

            list.Add(reader.GetString(1));
        }

        return result;
    }

    /// <summary>
    /// Everything column metadata cannot say. Every line here was added because the model got
    /// something wrong without it.
    /// </summary>
    /// <remarks>
    /// Written tersely on purpose. This block is the largest part of every prompt and is re-sent on
    /// every question, so its length is a per-question cost paid by both providers — tokens at the
    /// hosted gateway, and context window at the local model, where it is the difference between a
    /// reply that fits and one that stops mid-JSON. What was cut in getting it here was the prose
    /// explaining *why* each rule exists, which is a reader's need and not the model's; every fact,
    /// every exact value and every worked example survived, because those are what changed the
    /// output. The reasoning behind each line is preserved in git history rather than in the prompt.
    /// </remarks>
    private const string Semantics = """
        Column values — exact. A plausible-looking guess returns zero rows, which reads as "no data"
        when the data is there:
        - PeriodCode: 'M01'..'M12' = January..December, 'M13' = the annual average, 'S01'/'S02' =
          the two half-years. NOT a date: June 2025 is 'M06', never '202506'.
        - PeriodType: 'Month', 'Annual' or 'Semiannual'. RateType: 'SOFR'. IndicatorCode (in
          vw_LatestIndicator): 'CPI' or 'SOFR'. SeriesCode: 'CUUR0000SA0' — the only CPI series, so
          you rarely need to filter on it.

        Follow-ups. Earlier turns of this conversation appear above as user/assistant pairs, each
        assistant turn being the JSON you produced; turns older than those may be compressed into a
        single summary line each. A follow-up is often unreadable alone — "and the year before
        that?", "what about SOFR?", "same thing for 2023" — so read its referent out of the earlier
        statements and their parameters: after a query with @year = 2022, "the year before that" is
        2021. Then:
        - Always write a fresh statement. You are never shown the figures those queries returned and
          must never carry a number forward — every answer comes from a query run now.
        - Carry the reference only. A follow-up that names its own subject and period completely is
          a new question; ignore what came before.
        - If the referent is not in the conversation at all, return "sql": null with "refusal":
          "unanswerable". Do not guess a period, and do not reply with a question of your own — the
          JSON shape is the only reply that can be read, so a request for clarification arrives as a
          malfunction rather than as the reasonable question it is.

        A question about prices, inflation, rates or collection health over any window is answerable
        by definition: how many rows come back is a fact about the data, not a reason to decline. Do
        not refuse because a range is recent and CPI is published in arrears, do not shorten a window
        to one you expect to be populated, and do not ask which dates were meant. Reserve
        "unanswerable" for a subject these views do not hold at all — unemployment, GDP, equity
        prices, or a country other than the US.

        Examples. Resolve your own dates from today's date below rather than copying these.

        "What was CPI in June 2025?"
        {"sql": "SELECT ReferenceDate, IndexValue FROM analytics.vw_Cpi WHERE PeriodType = 'Month' AND ReferenceDate = @month",
         "parameters": {"@month": "2025-06-01"},
         "explanation": "Reads the figures the question asked for.",
         "refusal": null}

        "What was the year over year inflation rate for the last 3 months?" (asked on 2025-09-14)
        {"sql": "SELECT ReferenceDate, YearOverYearPct FROM analytics.vw_CpiMonthlyChange WHERE ReferenceDate >= @from AND ReferenceDate < @to ORDER BY ReferenceDate",
         "parameters": {"@from": "2025-06-01", "@to": "2025-09-01"},
         "explanation": "Reads the figures the question asked for.",
         "refusal": null}

        "What is the relation between CPI and SOFR for the year 2025?"
        {"sql": "SELECT c.ReferenceDate, c.YearOverYearPct AS InflationPct, s.AvgRatePercent FROM analytics.vw_CpiMonthlyChange AS c JOIN (SELECT DATEFROMPARTS(YEAR(EffectiveDate), MONTH(EffectiveDate), 1) AS MonthStart, AVG(RatePercent) AS AvgRatePercent FROM analytics.vw_Sofr WHERE EffectiveDate >= @from AND EffectiveDate < @to GROUP BY DATEFROMPARTS(YEAR(EffectiveDate), MONTH(EffectiveDate), 1)) AS s ON s.MonthStart = c.ReferenceDate WHERE c.ReferenceDate >= @from AND c.ReferenceDate < @to ORDER BY c.ReferenceDate",
         "parameters": {"@from": "2025-01-01", "@to": "2026-01-01"},
         "explanation": "Reads the figures the question asked for.",
         "refusal": null}

        "Between which months is the rate of change of SOFR the greatest in 2025?"
        {"sql": "SELECT TOP (1) DATEADD(month, -1, m.MonthStart) AS FromMonth, m.MonthStart AS ToMonth, m.PrevAvgRate, m.AvgRate, m.AvgRate - m.PrevAvgRate AS ChangeInPercentagePoints FROM (SELECT MonthStart, AvgRate, LAG(AvgRate) OVER (ORDER BY MonthStart) AS PrevAvgRate FROM (SELECT DATEFROMPARTS(YEAR(EffectiveDate), MONTH(EffectiveDate), 1) AS MonthStart, AVG(RatePercent) AS AvgRate FROM analytics.vw_Sofr WHERE EffectiveDate >= @from AND EffectiveDate < @to GROUP BY DATEFROMPARTS(YEAR(EffectiveDate), MONTH(EffectiveDate), 1)) AS monthly) AS m WHERE m.PrevAvgRate IS NOT NULL ORDER BY ABS(m.AvgRate - m.PrevAvgRate) DESC",
         "parameters": {"@from": "2025-01-01", "@to": "2026-01-01"},
         "explanation": "Reads the figures the question asked for.",
         "refusal": null}
        """;
}
