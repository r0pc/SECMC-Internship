using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.IntegrationTests.Collection;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DataIntelligence.IntegrationTests.Api;

/// <summary>
/// Hosts the real API over a real SQL Server database and seeds a known history to query
/// (SOW 11.1 — collector → database → API flow).
/// </summary>
/// <remarks>
/// The data is deliberately fixed rather than generated from today's date: an endpoint that
/// aggregates by month has to be asserted against known bucket boundaries, and a test whose
/// expected values move with the calendar cannot do that.
/// <para>
/// Series and categories come from the migration's seed data, so the tests exercise the same
/// reference rows a real deployment has.
/// </para>
/// </remarks>
public sealed class DashboardApiFixture : IAsyncLifetime
{
    /// <summary>CPI-U, all items, seasonally adjusted — monthly.</summary>
    public const int MonthlySeriesId = 2;

    /// <summary>CPI-U, all items less food and energy, NSA — the series the update tests mutate.</summary>
    public const int MutableSeriesId = 3;

    /// <summary>SOFR overnight rate — business-daily.</summary>
    public const int DailySeriesId = 5;

    /// <summary>First month of the seeded monthly history.</summary>
    public static readonly DateOnly MonthlyStart = new(2024, 1, 1);

    /// <summary>Months of monthly history seeded before the revised final month.</summary>
    public const int MonthlyCount = 23;

    /// <summary>The revised month: revision 0 at 311.5, superseded by revision 1 at 312.0.</summary>
    public static readonly DateOnly RevisedMonth = new(2025, 12, 1);

    public const decimal RevisedOriginalValue = 311.5m;
    public const decimal RevisedCurrentValue = 312.0m;

    /// <summary>An annual-average row, on a reference date no monthly row occupies.</summary>
    public static readonly DateOnly AnnualReferenceDate = new(2023, 1, 1);

    public const decimal AnnualValue = 299.9m;

    /// <summary>
    /// Collection timestamps, anchored to the present rather than to a literal date.
    /// </summary>
    /// <remarks>
    /// The collection-health endpoints measure a rolling window ending now, so runs dated in a
    /// fixed past would fall outside it and the health assertions would rot the moment the
    /// calendar moved past them. Truncated to whole seconds because <c>ScheduledForUtc</c> is
    /// <c>datetime2(0)</c>: without that, what is written and what is read back differ by the
    /// sub-second part.
    /// </remarks>
    private static readonly DateTime AnchorUtc = TruncateToSecond(DateTime.UtcNow);

    /// <summary>When the original vintage was collected.</summary>
    public static readonly DateTime FirstCollectionUtc = AnchorUtc.AddHours(-3);

    /// <summary>When the revision arrived.</summary>
    public static readonly DateTime RevisionCollectionUtc = AnchorUtc.AddHours(-2);

    /// <summary>When the most recent — and failed — run started.</summary>
    public static readonly DateTime FailedRunUtc = AnchorUtc.AddHours(-1);

    /// <summary>Business days seeded for the daily series, with their values.</summary>
    public static readonly (DateOnly Date, decimal Value)[] DailyPoints =
    [
        (new DateOnly(2025, 1, 2), 5.00m),
        (new DateOnly(2025, 1, 3), 5.10m),
        (new DateOnly(2025, 1, 6), 5.20m),
        (new DateOnly(2025, 1, 7), 5.30m),
        (new DateOnly(2025, 1, 8), 5.40m),
        (new DateOnly(2025, 2, 3), 4.00m),
        (new DateOnly(2025, 2, 4), 4.50m),
        (new DateOnly(2025, 2, 5), 5.00m)
    ];

    /// <summary>Double underscore is the configuration-section separator for environment variables.</summary>
    private const string ConnectionStringVariable = "ConnectionStrings__DataIntelligenceDb";

    private readonly CollectionDatabaseFixture _database = new();
    private WebApplicationFactory<Program>? _factory;

    public bool IsAvailable => _database.IsAvailable;

    public string UnavailableReason => _database.UnavailableReason;

    public HttpClient Client { get; private set; } = null!;

    /// <summary>Web defaults plus the converters the API serialises with.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public DataIntelligenceDbContext CreateContext() => _database.CreateContext();

    /// <summary>The value seeded for the nth month of the monthly series.</summary>
    public static decimal MonthlyValue(int monthIndex) => 300m + (0.5m * monthIndex);

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();

        if (!_database.IsAvailable)
        {
            return;
        }

        await SeedAsync();

        // The same channel a deployed environment uses (see backend/README.md). It beats the
        // project's appsettings.json in the configuration order, which an in-memory source added
        // through WithWebHostBuilder does not — that one is applied before the app's own JSON
        // files and loses to them.
        Environment.SetEnvironmentVariable(ConnectionStringVariable, _database.ConnectionString);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));

        Client = _factory.CreateClient();
    }

    /// <summary>
    /// Writes the history the tests assert against, through the production DbContext so every
    /// check constraint and unique index applies.
    /// </summary>
    private async Task SeedAsync()
    {
        await using var db = _database.CreateContext();

        var succeededFirst = NewRun(DataSource.BlsCpiId, FirstCollectionUtc, CollectionRunStatus.Succeeded);
        var succeededSecond = NewRun(DataSource.BlsCpiId, RevisionCollectionUtc, CollectionRunStatus.Succeeded);
        var failed = NewRun(
            DataSource.BlsCpiId,
            FailedRunUtc,
            CollectionRunStatus.Failed,
            CollectionFailureCategory.HttpError,
            "BLS returned 503.");
        var sofrRun = NewRun(DataSource.NyFedSofrId, FirstCollectionUtc, CollectionRunStatus.Succeeded);

        db.CollectionRuns.AddRange(succeededFirst, succeededSecond, failed, sofrRun);
        await db.SaveChangesAsync();

        // Monthly history, all first vintages.
        for (var month = 0; month < MonthlyCount; month++)
        {
            db.Observations.Add(NewObservation(
                MonthlySeriesId,
                MonthlyStart.AddMonths(month),
                PeriodType.Month,
                MonthlyValue(month),
                succeededFirst.CollectionRunId,
                FirstCollectionUtc));
        }

        // The annual average, in a year the monthly history does not cover. Real BLS M13 rows are
        // dated 1 January, and UQ_Observation_Current is keyed on (SeriesId, ReferenceDate)
        // without the period type — so an annual row and that year's January row cannot both be
        // current. Seeding it in an uncovered year is what lets the period filter be tested at
        // all; see SeriesPeriods.NativePeriodType.
        db.Observations.Add(NewObservation(
            MonthlySeriesId,
            AnnualReferenceDate,
            PeriodType.Annual,
            AnnualValue,
            succeededFirst.CollectionRunId,
            FirstCollectionUtc,
            sourcePeriodCode: "M13"));

        // The revised month, in the order the unique index requires: the superseded vintage
        // first, then the current one.
        var superseded = NewObservation(
            MonthlySeriesId,
            RevisedMonth,
            PeriodType.Month,
            RevisedOriginalValue,
            succeededFirst.CollectionRunId,
            FirstCollectionUtc);

        superseded.IsCurrent = false;
        superseded.SupersededAtUtc = RevisionCollectionUtc;

        db.Observations.Add(superseded);

        db.Observations.Add(NewObservation(
            MonthlySeriesId,
            RevisedMonth,
            PeriodType.Month,
            RevisedCurrentValue,
            succeededSecond.CollectionRunId,
            RevisionCollectionUtc,
            revisionNumber: 1));

        foreach (var (date, value) in DailyPoints)
        {
            db.Observations.Add(NewObservation(
                DailySeriesId, date, PeriodType.Day, value, sofrRun.CollectionRunId, FirstCollectionUtc));
        }

        await db.SaveChangesAsync();
    }

    private static CollectionRun NewRun(
        byte dataSourceId,
        DateTime startedAtUtc,
        CollectionRunStatus status,
        CollectionFailureCategory? failureCategory = null,
        string? errorMessage = null) =>
        new()
        {
            DataSourceId = dataSourceId,
            ScheduledForUtc = startedAtUtc,
            Attempt = 1,
            TriggerType = CollectionTriggerType.Scheduled,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = startedAtUtc.AddSeconds(4),
            Status = status,
            RequestUrl = "https://example.test/seed",
            HttpStatusCode = status == CollectionRunStatus.Failed ? (short)503 : (short)200,
            FailureCategory = failureCategory,
            ErrorMessage = errorMessage
        };

    private static DateTime TruncateToSecond(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Kind);

    private static Observation NewObservation(
        int seriesId,
        DateOnly referenceDate,
        PeriodType periodType,
        decimal value,
        long collectionRunId,
        DateTime collectedAtUtc,
        short revisionNumber = 0,
        string? sourcePeriodCode = null) =>
        new()
        {
            SeriesId = seriesId,
            ReferenceDate = referenceDate,
            PeriodType = periodType,
            SourcePeriodCode = sourcePeriodCode,
            RevisionNumber = revisionNumber,
            IsCurrent = true,
            Value = value,
            CollectionRunId = collectionRunId,
            CollectedAtUtc = collectedAtUtc,
            RowHash = SHA256.HashData(
                Encoding.UTF8.GetBytes($"{seriesId}|{referenceDate:O}|{value}|{revisionNumber}"))
        };

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        Environment.SetEnvironmentVariable(ConnectionStringVariable, null);
        await _database.DisposeAsync();
    }
}

/// <summary>
/// One database and one hosted API for every API test class. Building the schema costs seconds;
/// doing it per class would multiply that by the number of classes for no isolation the seeded
/// data does not already provide.
/// </summary>
[CollectionDefinition(Name)]
public sealed class DashboardApiCollection : ICollectionFixture<DashboardApiFixture>
{
    public const string Name = "DashboardApi";
}
