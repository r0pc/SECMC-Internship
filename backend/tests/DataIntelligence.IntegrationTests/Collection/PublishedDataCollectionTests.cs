using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Infrastructure.Collection;
using DataIntelligence.Infrastructure.Persistence;
using DataIntelligence.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataIntelligence.IntegrationTests.Collection;

/// <summary>
/// Runs one collection cycle per source over the real published extracts, so the tests that read
/// the result all see the same database and none of them has to be first.
/// </summary>
/// <remarks>
/// Collecting inside the fixture rather than inside each test is what makes the assertions
/// order-independent. A test that collected for itself would insert 1,559 rows the first time it
/// ran and find every one of them unchanged thereafter, so the suite would pass or fail depending
/// on which test the runner happened to start with.
/// </remarks>
public sealed class PublishedDataFixture : IAsyncLifetime
{
    /// <summary>
    /// After the last figure in either extract — June 2026 for CPI, 3 August 2026 for SOFR —
    /// because the validator correctly rejects a period dated beyond the collection time.
    /// </summary>
    public static readonly DateTime Cycle = new(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc);

    private readonly CollectionDatabaseFixture _database = new();

    public bool IsAvailable => _database.IsAvailable;

    public string UnavailableReason => _database.UnavailableReason;

    public CollectionSummary CpiSummary { get; private set; } = null!;

    public CollectionSummary SofrSummary { get; private set; } = null!;

    public DataIntelligenceDbContext CreateContext() => _database.CreateContext();

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();

        if (!_database.IsAvailable)
        {
            return;
        }

        CpiSummary = await PublishedDataCollector.CollectAsync(
            _database, DataSource.BlsCpiCode, PublishedData.BlsPayload(), Cycle);

        SofrSummary = await PublishedDataCollector.CollectAsync(
            _database, DataSource.NyFedSofrCode, PublishedData.SofrPayload(), Cycle);
    }

    public Task DisposeAsync() => _database.DisposeAsync();
}

/// <summary>
/// Checks what the collector actually put in the database against the files it came from
/// (SOW 11.1).
/// </summary>
/// <remarks>
/// The unit-level accuracy tests stop at the parsed record. These carry on through validation,
/// deduplication and the writers into SQL Server, so they also prove the schema accepts every
/// figure the publishers emit — a century of CPI at one and three decimal places, a month with no
/// value, and a year of SOFR days with four out-of-scope rates interleaved.
/// <para>
/// Nothing is stubbed except the HTTP fetch: the adapters, validator, writers, DbContext,
/// constraints and indexes are all production code.
/// </para>
/// </remarks>
public class PublishedDataCollectionTests : IClassFixture<PublishedDataFixture>
{
    private readonly PublishedDataFixture _fixture;

    public PublishedDataCollectionTests(PublishedDataFixture fixture)
    {
        if (!fixture.IsAvailable)
        {
            throw new InvalidOperationException(fixture.UnavailableReason);
        }

        _fixture = fixture;
    }

    // -------------------------------------------------------------------- CPI

    [Fact]
    public async Task TheWholeCpiExtractIsStoredExactlyAsPublished()
    {
        Assert.Equal(CollectionRunStatus.Succeeded, _fixture.CpiSummary.Status);
        Assert.Equal(PublishedData.CpiCells.Count, _fixture.CpiSummary.Inserted);
        Assert.Equal(0, _fixture.CpiSummary.Rejected);

        await using var db = _fixture.CreateContext();

        var stored = await db.CpiObservations
            .Where(o => o.IsCurrent)
            .ToDictionaryAsync(o => (o.ReferenceYear, o.PeriodCode));

        Assert.Equal(PublishedData.CpiCells.Count, stored.Count);

        // Every figure, compared against the file rather than against what the collector said it
        // did. A writer that dropped rows would still report a plausible summary.
        foreach (var cell in PublishedData.CpiCells)
        {
            Assert.True(stored.TryGetValue((cell.Year, cell.PeriodCode), out var row),
                $"{cell.Year}/{cell.PeriodCode} was not stored.");

            Assert.Equal(cell.Value, row!.IndexValue);
            Assert.Equal(cell.ReferenceDate, row.ReferenceDate);
            Assert.Equal(CpiObservation.SeriesCodeValue, row.SeriesCode);
            Assert.Equal(0, row.RevisionNumber);
        }
    }

    [Fact]
    public async Task TheAnnualAndSemiannualFiguresAreStoredAlongsideTheMonthsTheyShareADateWith()
    {
        await using var db = _fixture.CreateContext();

        // 2025 has all fifteen columns populated. M01, M13 and S01 are all dated 1 January, which
        // no (series, date) key could have held — this is the collision the two-table rewrite
        // exists to survive, run against the real file rather than a contrived row.
        var january = await db.CpiObservations
            .SingleAsync(o => o.ReferenceYear == 2025 && o.PeriodCode == "M01" && o.IsCurrent);
        var annual = await db.CpiObservations
            .SingleAsync(o => o.ReferenceYear == 2025 && o.PeriodCode == "M13" && o.IsCurrent);
        var firstHalf = await db.CpiObservations
            .SingleAsync(o => o.ReferenceYear == 2025 && o.PeriodCode == "S01" && o.IsCurrent);

        Assert.Equal(new DateOnly(2025, 1, 1), january.ReferenceDate);
        Assert.Equal(january.ReferenceDate, annual.ReferenceDate);
        Assert.Equal(january.ReferenceDate, firstHalf.ReferenceDate);

        Assert.Equal(317.671m, january.IndexValue);
        Assert.Equal(321.943m, annual.IndexValue);
        Assert.Equal(320.229m, firstHalf.IndexValue);

        Assert.Equal(PeriodType.Month, january.PeriodType);
        Assert.Equal(PeriodType.Annual, annual.PeriodType);
        Assert.Equal(PeriodType.Semiannual, firstHalf.PeriodType);
    }

    [Fact]
    public async Task AMonthThePublisherHasNotReleasedIsAbsentFromTheDatabase()
    {
        await using var db = _fixture.CreateContext();

        // October 2025 is blank in the extract. No row at all — not a zero, not a carried-forward
        // September, which would put a figure in the database that BLS never published.
        Assert.False(await db.CpiObservations
            .AnyAsync(o => o.ReferenceYear == 2025 && o.PeriodCode == "M10"));

        Assert.True(await db.CpiObservations
            .AnyAsync(o => o.ReferenceYear == 2025 && o.PeriodCode == "M09"));
        Assert.True(await db.CpiObservations
            .AnyAsync(o => o.ReferenceYear == 2025 && o.PeriodCode == "M11"));
    }

    // ------------------------------------------------------------------- SOFR

    [Fact]
    public async Task TheWholeSofrExtractIsStoredExactlyAsPublished()
    {
        // Partial, not failed: the CSV extract carries all five rates and the four out of scope
        // are rejected. A live API run is Succeeded instead, because that endpoint sends SOFR
        // alone — the difference is the payload, not the collector.
        Assert.Equal(CollectionRunStatus.PartialSuccess, _fixture.SofrSummary.Status);
        Assert.Equal(PublishedData.SofrOnly.Count, _fixture.SofrSummary.Inserted);
        Assert.Equal(PublishedData.OtherRates.Count, _fixture.SofrSummary.Rejected);

        await using var db = _fixture.CreateContext();

        var stored = await db.SofrDailyRates
            .Where(r => r.IsCurrent)
            .ToDictionaryAsync(r => r.EffectiveDate);

        Assert.Equal(PublishedData.SofrOnly.Count, stored.Count);

        foreach (var row in PublishedData.SofrOnly)
        {
            Assert.True(stored.TryGetValue(row.EffectiveDate, out var day),
                $"{row.EffectiveDate:yyyy-MM-dd} was not stored.");

            Assert.Equal(row.Rate, day!.RatePercent);
            Assert.Equal(row.Percentile1, day.Percentile1Percent);
            Assert.Equal(row.Percentile25, day.Percentile25Percent);
            Assert.Equal(row.Percentile75, day.Percentile75Percent);
            Assert.Equal(row.Percentile99, day.Percentile99Percent);
            Assert.Equal(row.Volume, day.VolumeUsdBillions);
            Assert.Equal(SofrDailyRate.RateTypeValue, day.RateType);
        }
    }

    [Fact]
    public async Task TheOtherRatesAreQuarantinedRatherThanStored()
    {
        await using var db = _fixture.CreateContext();

        // Nothing but SOFR reached the table — CK_Sofr_RateType would have refused it anyway,
        // which is the point of pinning the column.
        Assert.True(await db.SofrDailyRates.AllAsync(r => r.RateType == SofrDailyRate.RateTypeValue));

        var rejected = await db.RejectedObservations
            .Where(r => r.CollectionRunId == _fixture.SofrSummary.CollectionRunId)
            .ToListAsync();

        Assert.Equal(PublishedData.OtherRates.Count, rejected.Count);
        Assert.All(rejected, r => Assert.Equal(RejectionReason.UnknownSeries, r.Reason));

        // The evidence is specific enough to act on: which rate it was.
        Assert.Equal(
            ["BGCR", "EFFR", "OBFR", "TGCR"],
            rejected.Select(r => r.SeriesCode!).Distinct().Order(StringComparer.Ordinal));
    }
}

/// <summary>
/// Deduplication and revision handling at the volume the collector actually runs at (FR-3, FR-4).
/// </summary>
/// <remarks>
/// Each test gets its own database. Every one of them measures what a <em>first</em> collection
/// does against a <em>second</em>, so sharing a database would make each test's result depend on
/// which ran before it.
/// </remarks>
public class PublishedDataRevisionTests : IAsyncLifetime
{
    private readonly CollectionDatabaseFixture _database = new();

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();

        if (!_database.IsAvailable)
        {
            throw new InvalidOperationException(_database.UnavailableReason);
        }
    }

    public Task DisposeAsync() => _database.DisposeAsync();

    private Task<CollectionSummary> CollectAsync(
        string sourceCode, string payload, DateTime scheduledFor, bool storeRawPayload = true) =>
        PublishedDataCollector.CollectAsync(
            _database, sourceCode, payload, scheduledFor, storeRawPayload);

    [Fact]
    public async Task ReissuingTheWholeExtractWritesNothing()
    {
        // FR-3 at the scale it actually runs at. Raw-payload storage is off so the byte-identical
        // short-circuit cannot answer this — every one of the 1,559 figures has to be hashed and
        // compared against its stored vintage.
        var payload = PublishedData.BlsPayload();

        var first = await CollectAsync(
            DataSource.BlsCpiCode, payload, PublishedDataFixture.Cycle, storeRawPayload: false);
        var second = await CollectAsync(
            DataSource.BlsCpiCode, payload, PublishedDataFixture.Cycle.AddHours(1), storeRawPayload: false);

        Assert.Equal(PublishedData.CpiCells.Count, first.Inserted);

        Assert.Equal(0, second.Inserted);
        Assert.Equal(0, second.Revised);
        Assert.Equal(PublishedData.CpiCells.Count, second.Unchanged);

        await using var db = _database.CreateContext();

        Assert.Equal(PublishedData.CpiCells.Count, await db.CpiObservations.CountAsync());
    }

    [Fact]
    public async Task AnIdenticalResponseIsRecognisedBeforeItIsParsed()
    {
        var payload = PublishedData.SofrPayload();

        await CollectAsync(DataSource.NyFedSofrCode, payload, PublishedDataFixture.Cycle);
        var second = await CollectAsync(
            DataSource.NyFedSofrCode, payload, PublishedDataFixture.Cycle.AddHours(1));

        // The cheapest layer: the body hash matched, so nothing was parsed at all — not even the
        // 588 out-of-scope rate rows that would otherwise be rejected all over again.
        Assert.Equal(CollectionRunStatus.Succeeded, second.Status);
        Assert.Equal(0, second.Fetched);
        Assert.Equal(0, second.Rejected);
    }

    [Fact]
    public async Task ASingleCorrectedFigureIsTheOnlyThingRevised()
    {
        // What a real BLS revision looks like: one month restated, everything else reissued
        // unchanged. The run must report exactly one revision out of 1,559 figures, and the
        // superseded vintage must survive (FR-4).
        var original = PublishedData.CpiCells;

        var restated = original
            .Select(c => c is { Year: 2026, PeriodCode: "M06" }
                ? c with { Value = 334.100m, Text = "334.100", Footnote = "R" }
                : c)
            .ToList();

        await CollectAsync(DataSource.BlsCpiCode, PublishedData.BlsPayload(original),
            PublishedDataFixture.Cycle, storeRawPayload: false);

        var second = await CollectAsync(DataSource.BlsCpiCode, PublishedData.BlsPayload(restated),
            PublishedDataFixture.Cycle.AddHours(1), storeRawPayload: false);

        Assert.Equal(0, second.Inserted);
        Assert.Equal(1, second.Revised);
        Assert.Equal(original.Count - 1, second.Unchanged);

        await using var db = _database.CreateContext();

        var vintages = await db.CpiObservations
            .Where(o => o.ReferenceYear == 2026 && o.PeriodCode == "M06")
            .OrderBy(o => o.RevisionNumber)
            .ToListAsync();

        Assert.Equal(2, vintages.Count);

        Assert.Equal(333.952m, vintages[0].IndexValue);
        Assert.False(vintages[0].IsCurrent);
        Assert.NotNull(vintages[0].SupersededAtUtc);

        Assert.Equal(334.100m, vintages[1].IndexValue);
        Assert.True(vintages[1].IsCurrent);
        Assert.Equal("R", vintages[1].Footnotes);
    }
}

/// <summary>Runs one collection cycle with the production pipeline and a stubbed fetch.</summary>
internal static class PublishedDataCollector
{
    public static async Task<CollectionSummary> CollectAsync(
        CollectionDatabaseFixture database,
        string sourceCode,
        string payload,
        DateTime scheduledFor,
        bool storeRawPayload = true)
    {
        await using var db = database.CreateContext();

        var options = Options.Create(new CollectionOptions
        {
            StoreRawPayload = storeRawPayload,
            Bls = new BlsOptions()
        });

        var runner = new CollectionRunner(
            db,
            new StubFetcher(payload),
            [
                new BlsCpiAdapter(options, NullLogger<BlsCpiAdapter>.Instance),
                new SofrAdapter()
            ],
            [
                new CpiObservationWriter(db, NullLogger<CpiObservationWriter>.Instance),
                new SofrDailyRateWriter(db, NullLogger<SofrDailyRateWriter>.Instance)
            ],
            new AllowAllRobotsPolicy(),
            options,
            new FixedTimeProvider(scheduledFor),
            NullLogger<CollectionRunner>.Instance);

        return await runner.RunAsync(
            sourceCode, scheduledFor, CollectionTriggerType.Scheduled, null, CancellationToken.None);
    }

    private sealed class StubFetcher(string content) : ISourceFetcher
    {
        public Task<FetchResult> FetchAsync(
            SourceRequest request, DataSource source, CancellationToken cancellationToken) =>
            Task.FromResult(FetchResult.Success(content, "application/json", 200, content.Length));
    }

    private sealed class AllowAllRobotsPolicy : IRobotsPolicy
    {
        public Task<RobotsDecision> EvaluateAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult(RobotsDecision.Allowed("Test policy."));
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
