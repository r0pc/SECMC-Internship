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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cached;

    public SchemaContextProvider(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString(DependencyInjection.ConnectionStringName)
            ?? string.Empty;
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
        if (_cached is not null)
        {
            return _cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Re-checked inside the gate: several questions arriving together would otherwise each
            // build it, and the last one would win after the others had already paid for the trip.
            _cached ??= await BuildAsync(cancellationToken);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
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

        Every view already filters to the current vintage, so do not try to deduplicate revisions.

        The dialect is Microsoft SQL Server (T-SQL), which is not interchangeable with MySQL or
        PostgreSQL:
        - Row limits are 'SELECT TOP (n) ...'. There is no LIMIT clause; using one is a syntax error.
        - Date arithmetic is DATEADD/DATEDIFF, not INTERVAL.
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
