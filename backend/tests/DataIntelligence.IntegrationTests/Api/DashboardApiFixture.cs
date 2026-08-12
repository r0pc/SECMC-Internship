using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataIntelligence.Core.Analytics;
using DataIntelligence.Core.Collection;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.IntegrationTests.Collection;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
/// The sources come from the migration's seed data, so the tests exercise the same reference rows
/// a real deployment has. The series themselves are <c>SeriesCatalog</c> entries — fixed in code,
/// not rows — so there is nothing to seed for them.
/// </para>
/// </remarks>
public sealed class DashboardApiFixture : IAsyncLifetime
{
    /// <summary>The CPI series: monthly, plus the annual and semiannual rows BLS publishes.</summary>
    public const string CpiKey = SeriesCatalog.CpiKey;

    /// <summary>The SOFR rate: business-daily.</summary>
    public const string SofrKey = SeriesCatalog.SofrKey;

    /// <summary>SOFR transaction volume — a different measure of the same rows.</summary>
    public const string SofrVolumeKey = "sofr.volume";

    /// <summary>First month of the seeded monthly history.</summary>
    public static readonly DateOnly MonthlyStart = new(2024, 1, 1);

    /// <summary>Months of monthly history seeded before the revised final month.</summary>
    public const int MonthlyCount = 23;

    /// <summary>The revised month: revision 0 at 311.5, superseded by revision 1 at 312.0.</summary>
    public static readonly DateOnly RevisedMonth = new(2025, 12, 1);

    public const decimal RevisedOriginalValue = 311.5m;
    public const decimal RevisedCurrentValue = 312.0m;

    /// <summary>
    /// The annual average for 2024, dated 1 January — the same reference date as that year's
    /// January figure.
    /// </summary>
    /// <remarks>
    /// Seeded deliberately alongside the month it collides with. Under the previous single-fact-table
    /// design the two could not coexist, because the current-vintage index was keyed on
    /// (series, date) without the period. Keying on (year, period code) is what makes this row
    /// storable, so seeding it here is the test that the fix holds.
    /// </remarks>
    public static readonly short AnnualYear = 2024;

    public const decimal AnnualValue = 299.9m;

    /// <summary>
    /// Collection timestamps, anchored to the present rather than to a literal date.
    /// </summary>
    /// <remarks>
    /// The collection-health endpoints measure a rolling window ending now, so runs dated in a
    /// fixed past would fall outside it and the health assertions would rot the moment the
    /// calendar moved past them. Truncated to whole seconds because <c>ScheduledForPkt</c> is
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

    /// <summary>Business days seeded for SOFR, with their rate and volume.</summary>
    public static readonly (DateOnly Date, decimal Rate, decimal Volume)[] DailyPoints =
    [
        (new DateOnly(2025, 1, 2), 5.00m, 1000m),
        (new DateOnly(2025, 1, 3), 5.10m, 1010m),
        (new DateOnly(2025, 1, 6), 5.20m, 1020m),
        (new DateOnly(2025, 1, 7), 5.30m, 1030m),
        (new DateOnly(2025, 1, 8), 5.40m, 1040m),
        (new DateOnly(2025, 2, 3), 4.00m, 2000m),
        (new DateOnly(2025, 2, 4), 4.50m, 2010m),
        (new DateOnly(2025, 2, 5), 5.00m, 2020m)
    ];

    /// <summary>Double underscore is the configuration-section separator for environment variables.</summary>
    private const string ConnectionStringVariable = "ConnectionStrings__DataIntelligenceDb";

    private readonly CollectionDatabaseFixture _database = new();
    private WebApplicationFactory<Program>? _factory;

    public bool IsAvailable => _database.IsAvailable;

    public string UnavailableReason => _database.UnavailableReason;

    /// <summary>
    /// The client the data tests use, signed in as an administrator.
    /// </summary>
    /// <remarks>
    /// Every endpoint needs a token since FR-9, and these tests are about what the endpoints
    /// return rather than who may call them. An administrator can reach all of them, so the
    /// assertions stay about the data; who may reach what is asserted separately, in
    /// <c>AuthorizationTests</c>, where it is the subject rather than a precondition.
    /// </remarks>
    public HttpClient Client { get; private set; } = null!;

    /// <summary>Web defaults plus the converters the API serialises with.</summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public DataIntelligenceDbContext CreateContext() => _database.CreateContext();

    /// <summary>The value seeded for the nth month of the CPI history.</summary>
    public static decimal MonthlyValue(int monthIndex) => 300m + (0.5m * monthIndex);

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();

        if (!_database.IsAvailable)
        {
            return;
        }

        await SeedAsync();

        await using (var db = _database.CreateContext())
        {
            await TestAccounts.SeedAsync(db);
        }

        // The same channel a deployed environment uses (see backend/README.md). It beats the
        // project's appsettings.json in the configuration order, which an in-memory source added
        // through WithWebHostBuilder does not — that one is applied before the app's own JSON
        // files and loses to them.
        Environment.SetEnvironmentVariable(ConnectionStringVariable, _database.ConnectionString);

        // The API refuses to start without a signing key (FR-9), by design: every endpoint needs
        // one, so booting without it would mean a process that serves nothing but 401s.
        Environment.SetEnvironmentVariable(TestAccounts.SigningKeyVariable, TestAccounts.SigningKey);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));

        Anonymous = _factory.CreateClient();

        Client = await CreateClientAsAsync(TestAccounts.AdministratorEmail);
    }

    /// <summary>A client carrying no token, for asserting that an endpoint demands one.</summary>
    public HttpClient Anonymous { get; private set; } = null!;

    /// <summary>Signs in over HTTP and returns a client that presents the resulting token.</summary>
    public async Task<HttpClient> CreateClientAsAsync(string email)
    {
        var client = _factory!.CreateClient();
        var session = await TestAccounts.SignInAsync(client, email);

        return client.Authenticated(session.AccessToken);
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
            var referenceDate = MonthlyStart.AddMonths(month);

            db.CpiObservations.Add(NewCpi(
                referenceDate,
                $"M{referenceDate.Month:00}",
                PeriodType.Month,
                MonthlyValue(month),
                succeededFirst.CollectionRunId,
                FirstCollectionUtc));
        }

        // The annual average, sharing 1 January 2024 with that year's monthly figure.
        db.CpiObservations.Add(NewCpi(
            new DateOnly(AnnualYear, 1, 1),
            CpiPeriod.AnnualCode,
            PeriodType.Annual,
            AnnualValue,
            succeededFirst.CollectionRunId,
            FirstCollectionUtc));

        // The revised month, in the order the unique index requires: the superseded vintage
        // first, then the current one.
        var superseded = NewCpi(
            RevisedMonth,
            $"M{RevisedMonth.Month:00}",
            PeriodType.Month,
            RevisedOriginalValue,
            succeededFirst.CollectionRunId,
            FirstCollectionUtc);

        superseded.IsCurrent = false;
        superseded.SupersededAtPkt = RevisionCollectionUtc;

        db.CpiObservations.Add(superseded);

        db.CpiObservations.Add(NewCpi(
            RevisedMonth,
            $"M{RevisedMonth.Month:00}",
            PeriodType.Month,
            RevisedCurrentValue,
            succeededSecond.CollectionRunId,
            RevisionCollectionUtc,
            revisionNumber: 1));

        foreach (var (date, rate, volume) in DailyPoints)
        {
            db.SofrDailyRates.Add(NewSofr(date, rate, volume, sofrRun.CollectionRunId, FirstCollectionUtc));
        }

        await db.SaveChangesAsync();
    }

    private static CollectionRun NewRun(
        byte dataSourceId,
        DateTime startedAtPkt,
        CollectionRunStatus status,
        CollectionFailureCategory? failureCategory = null,
        string? errorMessage = null) =>
        new()
        {
            DataSourceId = dataSourceId,
            ScheduledForPkt = startedAtPkt,
            Attempt = 1,
            TriggerType = CollectionTriggerType.Scheduled,
            StartedAtPkt = startedAtPkt,
            CompletedAtPkt = startedAtPkt.AddSeconds(4),
            Status = status,
            RequestUrl = "https://example.test/seed",
            HttpStatusCode = status == CollectionRunStatus.Failed ? (short)503 : (short)200,
            FailureCategory = failureCategory,
            ErrorMessage = errorMessage
        };

    private static DateTime TruncateToSecond(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond), value.Kind);

    private static CpiObservation NewCpi(
        DateOnly referenceDate,
        string periodCode,
        PeriodType periodType,
        decimal indexValue,
        long collectionRunId,
        DateTime collectedAtPkt,
        short revisionNumber = 0) =>
        new()
        {
            ReferenceDate = referenceDate,
            ReferenceYear = (short)referenceDate.Year,
            PeriodCode = periodCode,
            PeriodType = periodType,
            IndexValue = indexValue,
            RevisionNumber = revisionNumber,
            IsCurrent = true,
            CollectionRunId = collectionRunId,
            CollectedAtPkt = collectedAtPkt,
            RowHash = Hash($"cpi|{referenceDate:O}|{periodCode}|{indexValue}|{revisionNumber}")
        };

    private static SofrDailyRate NewSofr(
        DateOnly effectiveDate,
        decimal rate,
        decimal volume,
        long collectionRunId,
        DateTime collectedAtPkt) =>
        new()
        {
            EffectiveDate = effectiveDate,
            RatePercent = rate,
            Percentile1Percent = rate - 0.05m,
            Percentile25Percent = rate - 0.02m,
            Percentile75Percent = rate + 0.02m,
            Percentile99Percent = rate + 0.05m,
            VolumeUsdBillions = volume,
            RevisionNumber = 0,
            IsCurrent = true,
            CollectionRunId = collectionRunId,
            CollectedAtPkt = collectedAtPkt,
            RowHash = Hash($"sofr|{effectiveDate:O}|{rate}|{volume}")
        };

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        Environment.SetEnvironmentVariable(ConnectionStringVariable, null);
        Environment.SetEnvironmentVariable(TestAccounts.SigningKeyVariable, null);
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
