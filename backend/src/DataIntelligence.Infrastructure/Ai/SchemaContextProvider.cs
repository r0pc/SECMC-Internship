// backend/src/DataIntelligence.Infrastructure/Ai/SchemaContextProvider.cs
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
            ["vw_CpiMonthlyChange"] = "Monthly CPI with month-over-month and year-over-year % change "
                + "(YearOverYearPct is the headline inflation rate).",
            ["vw_CpiAnnual"] = "The annual and semiannual averages BLS publishes, one row per year.",
            ["vw_Sofr"] = "Current SOFR daily rates. VolumeUsdBillions is in billions of dollars, not dollars.",
            ["vw_SofrAnnual"] = "SOFR summarised per calendar year.",
            ["vw_LatestIndicator"] = "The single latest CPI row and the single latest SOFR row, side by side.",
            ["vw_CpiRevision"] = "Every vintage of a CPI period that has ever been revised.",
            ["vw_SofrRevision"] = "Every vintage of a SOFR date that has ever been revised.",
            ["vw_CollectionHealth"] = "Daily collector health per source, rolling 30 days."
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
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);

        var coverage = await ReadCoverageAsync(cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine();
        builder.Append("Today's date is ").Append(today.ToString("yyyy-MM-dd")).AppendLine(" (UTC).");
        builder.AppendLine(
            "Resolve every relative date against it — \"last month\", \"this year\", \"the last 6 "
            + "months\", \"year to date\" — and write the resulting dates into the query as "
            + "parameters. Never ask the user which dates they meant.");
        builder.AppendLine();

        if (coverage.Count > 0)
        {
            builder.AppendLine("What the database currently holds:");

            foreach (var (label, earliest, latest) in coverage)
            {
                builder.Append("- ").Append(label).Append(": ")
                    .Append(earliest.ToString("yyyy-MM-dd")).Append(" to ")
                    .Append(latest.ToString("yyyy-MM-dd")).AppendLine();
            }

            builder.AppendLine();
            builder.AppendLine(
                "A publisher releases in arrears, so the newest figure is normally older than "
                + "today. If a relative range falls partly or wholly outside what is held, still "
                + "write the query for the range asked for — an empty or short result is the "
                + "honest answer, and inventing a different range to fill it is not.");
        }

        return builder.ToString();
    }

    /// <summary>The dataset date axes worth reporting a range for.</summary>
    private static readonly (string Label, string View, string Column)[] DateAxes =
    [
        ("CPI (analytics.vw_Cpi, ReferenceDate)", "analytics.vw_Cpi", "ReferenceDate"),
        ("SOFR (analytics.vw_Sofr, EffectiveDate)", "analytics.vw_Sofr", "EffectiveDate"),
    ];

    /// <summary>The first and last date held per dataset.</summary>
    /// <remarks>
    /// One query per dataset rather than a single UNION, so a view that is missing costs its own
    /// line and not the whole block. A partially deployed database should still be able to tell
    /// the model what it does have.
    /// </remarks>
    private async Task<IReadOnlyList<(string Label, DateOnly Earliest, DateOnly Latest)>>
        ReadCoverageAsync(CancellationToken cancellationToken)
    {
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

            builder.AppendLine();
        }

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
    private const string Semantics = """
        What the words in a question map to. These are the terms people actually ask in, and none
        of them appear as a column name — without the mapping the question looks unanswerable and
        gets refused:

        - "inflation", "inflation rate", "how much have prices risen" → YearOverYearPct in
          analytics.vw_CpiMonthlyChange. Month-over-month inflation is MonthOverMonthPct.
        - "cost of living", "price level", "CPI" → IndexValue in analytics.vw_Cpi.
        - "interest rate", "overnight rate", "borrowing rate", "repo rate" → RatePercent in
          analytics.vw_Sofr.
        - "trading volume", "how much was borrowed" → VolumeUsdBillions in analytics.vw_Sofr.
        - "is collection working", "did anything fail", "data freshness" →
          analytics.vw_CollectionHealth.
        - "latest", "most recent", "current" for a single headline figure →
          analytics.vw_LatestIndicator.

        Column values — use these exactly. Guessing a plausible-looking code returns zero rows,
        which reads as "no data" when the data is in fact there:

        - PeriodCode is 'M01'..'M12' for January..December, 'M13' for the annual average, and
          'S01'/'S02' for the two half-years. It is NOT a date: June 2025 is 'M06', never '202506'.
        - PeriodType is 'Month', 'Annual' or 'Semiannual'.
        - SeriesCode is 'CUUR0000SA0'. There is only one CPI series; you rarely need to filter on it.
        - RateType is 'SOFR'.
        - IndicatorCode in vw_LatestIndicator is 'CPI' or 'SOFR'.

        Prefer ReferenceDate (CPI) and EffectiveDate (SOFR) over period codes. Both are dates, and
        each period is stored on the first day of the period it covers — so June 2025 CPI is
        ReferenceDate = '2025-06-01'.

        For a range, use a half-open window — >= the first day and < the first day of the next
        period. It is the one form that is correct for both datasets: CPI stores one row on the
        first of the month, while SOFR stores a row per business day, so BETWEEN with a guessed
        month-end silently drops the 31st.

        "The average SOFR rate last month" is therefore:
        SELECT AVG(RatePercent) FROM analytics.vw_Sofr
        WHERE EffectiveDate >= @from AND EffectiveDate < @to
        with @from and @to as the first days of last month and this month.

        Every view already filters to the current vintage, so do not try to deduplicate revisions.

        The dialect is Microsoft SQL Server (T-SQL), which is not interchangeable with MySQL or
        PostgreSQL:
        - Row limits are 'SELECT TOP (n) ...'. There is no LIMIT clause; using one is a syntax error.
        - Date arithmetic is DATEADD/DATEDIFF, not INTERVAL. Prefer resolving relative dates
          yourself against today's date and passing them as parameters — a literal date in a
          parameter is easier to review than DATEADD nested around GETDATE().
        - Group a date column by month with DATEFROMPARTS(YEAR(c), MONTH(c), 1), not by a string.
        - String concatenation is + or CONCAT(), not ||.

        Rules:
        - Write exactly one SELECT statement. No comments, no semicolons, no other statement type.
        - Never reference any table or view not listed above.
        - If the question cannot be answered from these views, respond with SQL: null.

        Example — "What was CPI in June 2025?":
        {"sql": "SELECT ReferenceDate, IndexValue FROM analytics.vw_Cpi WHERE PeriodType = 'Month' AND ReferenceDate = @month",
         "parameters": {"@month": "2025-06-01"},
         "explanation": "Reads the current monthly CPI index value for the given month from vw_Cpi.",
         "refusal": null}
        """;
}
