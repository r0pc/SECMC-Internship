using DataIntelligence.Core.Collection;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Infrastructure.Collection;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataIntelligence.IntegrationTests.Collection;

/// <summary>
/// Collector → database flow against real SQL Server (SOW 11.1). The fetcher is stubbed so the
/// tests are deterministic and never call the live publishers; the adapter contract, validator,
/// runner, dataset writers and schema underneath are all production code.
/// </summary>
public class CollectionRunnerTests : IClassFixture<CollectionDatabaseFixture>
{
    private static int _slotCounter;

    private readonly CollectionDatabaseFixture _fixture;
    private readonly DateOnly _sofrDate;
    private readonly short _cpiYear;
    private readonly DateTime _cycle1;
    private readonly DateTime _cycle2;

    public CollectionRunnerTests(CollectionDatabaseFixture fixture)
    {
        _fixture = fixture;

        // These tests require a real SQL Server, which CI provisions as a service container.
        // Failing with an actionable message beats passing silently and reporting coverage the
        // run never had.
        if (!fixture.IsAvailable)
        {
            throw new InvalidOperationException(fixture.UnavailableReason);
        }

        // The class shares one database, and each dataset is now a single pinned table, so
        // isolation comes from the period rather than from a per-test series: each test writes
        // its own business day and its own CPI year, and gets its own schedule slot so attempt
        // numbering cannot collide either.
        var slot = Interlocked.Increment(ref _slotCounter);

        _sofrDate = new DateOnly(2026, 1, 1).AddDays(slot);
        _cpiYear = (short)(1950 + slot);

        // Collection time must sit after the observed period, or the validator correctly rejects
        // every record as a future publication.
        _cycle1 = new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc).AddHours(slot * 24);
        _cycle2 = _cycle1.AddHours(1);
    }

    private SofrDailyRateRecord Sofr(decimal rate, string? revisionIndicator = null) => new()
    {
        EffectiveDate = _sofrDate,
        RatePercent = rate,
        Percentile1Percent = rate - 0.05m,
        Percentile99Percent = rate + 0.05m,
        VolumeUsdBillions = 3000m,
        RevisionIndicator = revisionIndicator
    };

    private CpiObservationRecord Cpi(string periodCode, decimal indexValue) => new()
    {
        ReferenceYear = _cpiYear,
        PeriodCode = periodCode,
        PeriodType = CpiPeriod.PeriodTypeFor(periodCode),
        ReferenceDate = CpiPeriod.ReferenceDateFor(_cpiYear, periodCode),
        IndexValue = indexValue
    };

    private int _payloadSequence;

    /// <summary>
    /// Runs one cycle in its own scope, as the Worker does.
    /// </summary>
    /// <param name="payload">
    /// Left null, every call gets a distinct body. The class shares one source, so a fixed
    /// default would trip the runner's byte-identical-payload short-circuit against an unrelated
    /// test's run — and each test would silently stop exercising what it claims to.
    /// </param>
    private async Task<CollectionSummary> RunAsync(
        DateTime scheduledFor,
        ParseResult? parseResult = null,
        FetchResult? forcedFailure = null,
        string? payload = null,
        CollectionTriggerType trigger = CollectionTriggerType.Scheduled,
        string sourceCode = DataSource.NyFedSofrCode)
    {
        payload ??= $$"""{"slot":"{{_sofrDate:yyyy-MM-dd}}","seq":{{++_payloadSequence}}}""";

        await using var db = _fixture.CreateContext();

        var runner = new CollectionRunner(
            db,
            new StubFetcher(payload, forcedFailure),
            [new StubAdapter(sourceCode, parseResult ?? new ParseResult([], [], 0))],
            [
                new CpiObservationWriter(db, NullLogger<CpiObservationWriter>.Instance),
                new SofrDailyRateWriter(db, NullLogger<SofrDailyRateWriter>.Instance)
            ],
            new AllowAllRobotsPolicy(),
            Options.Create(new CollectionOptions()),
            new FixedTimeProvider(scheduledFor),
            NullLogger<CollectionRunner>.Instance);

        return await runner.RunAsync(sourceCode, scheduledFor, trigger, null, CancellationToken.None);
    }

    private Task<CollectionSummary> RunCpiAsync(DateTime scheduledFor, params CpiObservationRecord[] records) =>
        RunAsync(scheduledFor,
            new ParseResult(records, [], records.Length),
            sourceCode: DataSource.BlsCpiCode);

    [Fact]
    public async Task FirstCycle_StoresTheDayAsRevisionZero()
    {
        var summary = await RunAsync(_cycle1, new ParseResult([Sofr(3.65m)], [], 1));

        Assert.Equal(CollectionRunStatus.Succeeded, summary.Status);
        Assert.Equal(1, summary.Inserted);

        await using var verify = _fixture.CreateContext();
        var row = await verify.SofrDailyRates.SingleAsync(r => r.EffectiveDate == _sofrDate);

        Assert.Equal(3.65m, row.RatePercent);
        Assert.Equal(0, row.RevisionNumber);
        Assert.True(row.IsCurrent);
        Assert.Null(row.SupersededAtUtc);

        // The measures land as columns of that one row, not as separate rows.
        Assert.Equal(3.60m, row.Percentile1Percent);
        Assert.Equal(3.70m, row.Percentile99Percent);
        Assert.Equal(3000m, row.VolumeUsdBillions);

        // FR-6: the collection timestamp, distinct from the day the rate describes.
        Assert.Equal(_cycle1, row.CollectedAtUtc);
    }

    [Fact]
    public async Task ReissuingTheSameDay_WritesNothing()
    {
        // FR-3. Polling business-daily data hourly means this is the common path.
        await RunAsync(_cycle1, new ParseResult([Sofr(3.65m)], [], 1));

        var summary = await RunAsync(_cycle2, new ParseResult([Sofr(3.65m)], [], 1));

        Assert.Equal(0, summary.Inserted);
        Assert.Equal(0, summary.Revised);
        Assert.Equal(1, summary.Unchanged);

        await using var verify = _fixture.CreateContext();
        Assert.Equal(1, await verify.SofrDailyRates.CountAsync(r => r.EffectiveDate == _sofrDate));
    }

    [Fact]
    public async Task ARevisedRate_SupersedesTheOldVintageAndKeepsIt()
    {
        // FR-4: history is retained, never overwritten.
        await RunAsync(_cycle1, new ParseResult([Sofr(3.65m)], [], 1));

        var summary = await RunAsync(_cycle2, new ParseResult([Sofr(3.68m, "Y")], [], 1));

        Assert.Equal(1, summary.Revised);
        Assert.Equal(0, summary.Inserted);

        await using var verify = _fixture.CreateContext();
        var vintages = await verify.SofrDailyRates
            .Where(r => r.EffectiveDate == _sofrDate)
            .OrderBy(r => r.RevisionNumber)
            .ToListAsync();

        Assert.Equal(2, vintages.Count);

        Assert.Equal(3.65m, vintages[0].RatePercent);
        Assert.False(vintages[0].IsCurrent);
        Assert.NotNull(vintages[0].SupersededAtUtc);

        Assert.Equal(3.68m, vintages[1].RatePercent);
        Assert.Equal(1, vintages[1].RevisionNumber);
        Assert.True(vintages[1].IsCurrent);
        Assert.Equal("Y", vintages[1].RevisionIndicator);
    }

    [Fact]
    public async Task AVolumeOnlyCorrection_IsStillARevision()
    {
        // The reason the hash covers every measure: a restatement that moved only the volume is
        // still a restatement, and hashing the rate alone would file it as unchanged.
        await RunAsync(_cycle1, new ParseResult([Sofr(3.65m)], [], 1));

        var corrected = Sofr(3.65m) with { VolumeUsdBillions = 3100m };

        var summary = await RunAsync(_cycle2, new ParseResult([corrected], [], 1));

        Assert.Equal(1, summary.Revised);
    }

    [Fact]
    public async Task ExactlyOneVintageStaysCurrentAcrossRepeatedRevisions()
    {
        // The integrity rule the dashboards depend on: UQ_SofrDailyRate_Current would reject a
        // second live vintage outright, so this proves the supersede-then-append order holds.
        await RunAsync(_cycle1, new ParseResult([Sofr(1m)], [], 1));
        await RunAsync(_cycle2, new ParseResult([Sofr(2m)], [], 1));
        await RunAsync(_cycle2.AddHours(1), new ParseResult([Sofr(3m)], [], 1));

        await using var verify = _fixture.CreateContext();
        var live = await verify.SofrDailyRates
            .Where(r => r.EffectiveDate == _sofrDate && r.IsCurrent)
            .ToListAsync();

        Assert.Single(live);
        Assert.Equal(3m, live[0].RatePercent);
        Assert.Equal(2, live[0].RevisionNumber);
    }

    [Fact]
    public async Task TheAnnualAverageCoexistsWithJanuary()
    {
        // The collision the two-table rewrite exists to survive: M13 and M01 share a reference
        // date, and under the previous (series, date) current-vintage key one silently
        // overwrote the other. Keying on (year, period code) is what makes both storable.
        var summary = await RunCpiAsync(_cycle1,
            Cpi("M01", 100.0m),
            Cpi(CpiPeriod.AnnualCode, 105.5m),
            Cpi(CpiPeriod.FirstHalfCode, 102.0m));

        Assert.Equal(3, summary.Inserted);

        await using var verify = _fixture.CreateContext();
        var rows = await verify.CpiObservations
            .Where(o => o.ReferenceYear == _cpiYear)
            .OrderBy(o => o.PeriodCode)
            .ToListAsync();

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(new DateOnly(_cpiYear, 1, 1), r.ReferenceDate));
        Assert.All(rows, r => Assert.True(r.IsCurrent));

        Assert.Equal([PeriodType.Month, PeriodType.Annual, PeriodType.Semiannual],
            rows.Select(r => r.PeriodType));
    }

    [Fact]
    public async Task ARevisedMonth_DoesNotDisturbTheAnnualAverageSharingItsDate()
    {
        await RunCpiAsync(_cycle1, Cpi("M01", 100.0m), Cpi(CpiPeriod.AnnualCode, 105.5m));

        var summary = await RunCpiAsync(_cycle2, Cpi("M01", 100.4m), Cpi(CpiPeriod.AnnualCode, 105.5m));

        Assert.Equal(1, summary.Revised);
        Assert.Equal(1, summary.Unchanged);

        await using var verify = _fixture.CreateContext();

        var january = await verify.CpiObservations
            .SingleAsync(o => o.ReferenceYear == _cpiYear && o.PeriodCode == "M01" && o.IsCurrent);
        var annual = await verify.CpiObservations
            .SingleAsync(o => o.ReferenceYear == _cpiYear
                && o.PeriodCode == CpiPeriod.AnnualCode && o.IsCurrent);

        Assert.Equal(100.4m, january.IndexValue);
        Assert.Equal(1, january.RevisionNumber);
        Assert.Equal(105.5m, annual.IndexValue);
        Assert.Equal(0, annual.RevisionNumber);
    }

    [Fact]
    public async Task InvalidRecords_AreRejectedRatherThanStored()
    {
        // A rate outside the sanity band is the decimal-shift parse bug; it costs one logged
        // rejection instead of an opaque constraint violation that aborts the batch — and the
        // sound record in the same payload still lands.
        //
        // The rejected record carries the neighbouring day so that nothing this test writes can
        // land on another test's slot: the tests share one table, and a stray row with the same
        // measures would make that test's insert deduplicate against it.
        var invalid = Sofr(365m) with { EffectiveDate = _sofrDate.AddDays(1) };

        var summary = await RunAsync(_cycle1, new ParseResult([invalid, Sofr(3.65m)], [], 2));

        Assert.Equal(1, summary.Inserted);
        Assert.Equal(1, summary.Rejected);
        Assert.Equal(CollectionRunStatus.PartialSuccess, summary.Status);

        await using var verify = _fixture.CreateContext();
        var rejection = await verify.RejectedObservations
            .SingleAsync(r => r.CollectionRunId == summary.CollectionRunId);

        Assert.Equal(RejectionReason.OutOfRange, rejection.Reason);
        Assert.Equal(SofrDailyRate.RateTypeValue, rejection.SeriesCode);
    }

    [Fact]
    public async Task FetchFailure_IsRecordedAndCategorised()
    {
        // FR-2: logged with a category, and nothing throws.
        var failure = FetchResult.Failure(
            CollectionFailureCategory.Timeout, "Request exceeded the 30s timeout.");

        var summary = await RunAsync(_cycle1, forcedFailure: failure);

        Assert.Equal(CollectionRunStatus.Failed, summary.Status);
        Assert.Equal(CollectionFailureCategory.Timeout, summary.FailureCategory);
        Assert.True(summary.ShouldRetry);

        await using var verify = _fixture.CreateContext();
        var run = await verify.CollectionRuns.SingleAsync(r => r.CollectionRunId == summary.CollectionRunId);

        Assert.Equal(CollectionFailureCategory.Timeout, run.FailureCategory);
        Assert.NotNull(run.CompletedAtUtc);
    }

    [Fact]
    public async Task RateLimiting_IsNotRetried()
    {
        // The remedy is a registration key or a smaller budget, not a faster retry.
        var failure = FetchResult.Failure(
            CollectionFailureCategory.RateLimited, "BLS daily threshold reached.");

        var summary = await RunAsync(_cycle1, forcedFailure: failure);

        Assert.Equal(CollectionFailureCategory.RateLimited, summary.FailureCategory);
        Assert.False(summary.ShouldRetry);
    }

    [Fact]
    public async Task AnEmptyPayload_IsReportedAsAContractChange()
    {
        // Distinguishes "the publisher changed its API" from "the publisher is down".
        var summary = await RunAsync(_cycle1, new ParseResult([], [], 0));

        Assert.Equal(CollectionRunStatus.Failed, summary.Status);
        Assert.Equal(CollectionFailureCategory.SchemaChanged, summary.FailureCategory);
        Assert.False(summary.ShouldRetry);
    }

    [Fact]
    public async Task PartialSuccess_WhenSomeRecordsAreRejectedByTheAdapter()
    {
        // A rejection the adapter raised, alongside a record it accepted: data landed, something
        // was lost, and the run says so rather than reporting a clean cycle.
        var rejection = new RejectedFragment(
            "EFFR", null, RejectionReason.UnknownSeries, "Record is of type 'EFFR', not SOFR.", "{}");

        var summary = await RunAsync(_cycle1, new ParseResult([Sofr(3.65m)], [rejection], 2));

        Assert.Equal(CollectionRunStatus.PartialSuccess, summary.Status);
        Assert.Equal(1, summary.Inserted);
        Assert.Equal(1, summary.Rejected);
    }

    [Fact]
    public async Task AnIdenticalPayload_ShortCircuitsBeforeParsing()
    {
        // The cheapest possible dedup: byte-identical bodies are recognised from the stored
        // payload hash, so nothing is even parsed. Scoped to this test's day so it cannot
        // collide with another test's stored payload.
        var body = $$"""{"effectiveDate":"{{_sofrDate:yyyy-MM-dd}}"}""";

        await RunAsync(_cycle1, new ParseResult([Sofr(1m)], [], 1), payload: body);

        // The adapter would return a different value, but it is never consulted.
        var summary = await RunAsync(_cycle2, new ParseResult([Sofr(9m)], [], 1), payload: body);

        Assert.Equal(CollectionRunStatus.Succeeded, summary.Status);
        Assert.Equal(0, summary.Fetched);

        await using var verify = _fixture.CreateContext();
        var rates = await verify.SofrDailyRates
            .Where(r => r.EffectiveDate == _sofrDate)
            .Select(r => r.RatePercent)
            .ToListAsync();

        Assert.Equal([1m], rates);
    }

    [Fact]
    public async Task RawPayloadIsStoredForDiagnosis()
    {
        var summary = await RunAsync(_cycle1, new ParseResult([Sofr(1m)], [], 1));

        await using var verify = _fixture.CreateContext();
        var payload = await verify.RawPayloads.SingleAsync(p => p.CollectionRunId == summary.CollectionRunId);

        Assert.Equal(32, payload.ContentHash.Length);
        Assert.True(payload.SizeBytes > 0);
        Assert.True(payload.CompressedContent.Length > 0);
    }

    [Fact]
    public async Task RetryOfTheSameCycle_GetsADistinctAttemptNumber()
    {
        await RunAsync(_cycle1, new ParseResult([Sofr(1m)], [], 1));
        await RunAsync(_cycle1, new ParseResult([Sofr(1m)], [], 1), trigger: CollectionTriggerType.Retry);

        await using var verify = _fixture.CreateContext();
        var attempts = await verify.CollectionRuns
            .Where(r => r.DataSourceId == DataSource.NyFedSofrId && r.ScheduledForUtc == _cycle1)
            .Select(r => r.Attempt)
            .OrderBy(a => a)
            .ToListAsync();

        Assert.Equal([(byte)1, (byte)2], attempts);
    }

    [Fact]
    public async Task UnknownSourceCode_IsSkippedNotCrashed()
    {
        var summary = await RunAsync(_cycle1, sourceCode: "NO_SUCH_SOURCE");

        Assert.Equal(CollectionRunStatus.Skipped, summary.Status);
        Assert.Null(summary.CollectionRunId);
    }

    [Fact]
    public async Task ASourceWithNoWriter_IsSkippedBeforeAnyRunIsRecorded()
    {
        // A dataset with an adapter but nowhere to put the result would otherwise fetch, parse,
        // and record a run that looks like it did something.
        await using var db = _fixture.CreateContext();

        var runner = new CollectionRunner(
            db,
            new StubFetcher("{}", null),
            [new StubAdapter(DataSource.NyFedSofrCode, new ParseResult([], [], 0))],
            [],
            new AllowAllRobotsPolicy(),
            Options.Create(new CollectionOptions()),
            new FixedTimeProvider(_cycle1),
            NullLogger<CollectionRunner>.Instance);

        var summary = await runner.RunAsync(
            DataSource.NyFedSofrCode, _cycle1, CollectionTriggerType.Scheduled, null, CancellationToken.None);

        Assert.Equal(CollectionRunStatus.Skipped, summary.Status);
        Assert.Null(summary.CollectionRunId);
    }

    private sealed class StubFetcher(string content, FetchResult? forcedFailure) : ISourceFetcher
    {
        public Task<FetchResult> FetchAsync(
            SourceRequest request, DataSource source, CancellationToken cancellationToken) =>
            Task.FromResult(forcedFailure ?? FetchResult.Success(content, "application/json", 200, 1));
    }

    private sealed class StubAdapter(string sourceCode, ParseResult result) : ISourceAdapter
    {
        public string SourceCode => sourceCode;

        public SourceRequest BuildRequest(SourceRequestContext context) =>
            SourceRequest.Get("https://example.test/api");

        public ParseResult Parse(string content) => result;
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
