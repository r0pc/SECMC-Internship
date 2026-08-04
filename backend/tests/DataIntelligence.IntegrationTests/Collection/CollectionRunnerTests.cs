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
/// tests are deterministic and never call the live publishers; the adapter, validator, runner
/// and schema underneath are all production code.
/// </summary>
public class CollectionRunnerTests : IClassFixture<CollectionDatabaseFixture>
{
    private static int _slotCounter;

    private readonly CollectionDatabaseFixture _fixture;
    private readonly string _seriesCode;
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

        // The class shares one database, so each test gets its own series and schedule slot;
        // otherwise tests would collide on UQ_Series_Code and on attempt numbering.
        var slot = Interlocked.Increment(ref _slotCounter);
        _seriesCode = $"TEST_{slot:D4}";

        // Collection time must sit after the observed period, or the validator correctly
        // rejects every record as a future publication.
        _cycle1 = new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc).AddHours(slot * 24);
        _cycle2 = _cycle1.AddHours(1);

        SeedSeries();
    }

    private void SeedSeries()
    {
        using var db = _fixture.CreateContext();

        db.Series.Add(new Series
        {
            DataSourceId = DataSource.NyFedSofrId,
            SeriesCode = _seriesCode,
            IsSourceAssignedCode = true,
            Title = $"Test series {_seriesCode}",
            Unit = "Percent per annum",
            Frequency = SeriesFrequency.BusinessDaily,
            SeasonalAdjustment = SeasonalAdjustment.NotApplicable
        });

        db.SaveChanges();
    }

    private static readonly DateOnly Period = new(2026, 6, 1);

    private ObservationRecord Record(decimal value, string? annotation = null) => new()
    {
        SeriesCode = _seriesCode,
        ReferenceDate = Period,
        PeriodType = PeriodType.Month,
        SourcePeriodCode = "M06",
        Value = value,
        SourceAnnotation = annotation
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
        CollectionTriggerType trigger = CollectionTriggerType.Scheduled)
    {
        payload ??= $$"""{"series":"{{_seriesCode}}","seq":{{++_payloadSequence}}}""";

        await using var db = _fixture.CreateContext();

        var runner = new CollectionRunner(
            db,
            new StubFetcher(payload, forcedFailure),
            [new StubAdapter(parseResult ?? new ParseResult([], [], 0))],
            new AllowAllRobotsPolicy(),
            Options.Create(new CollectionOptions()),
            new FixedTimeProvider(scheduledFor),
            NullLogger<CollectionRunner>.Instance);

        return await runner.RunAsync(
            DataSource.NyFedSofrCode, scheduledFor, trigger, CancellationToken.None);
    }

    [Fact]
    public async Task FirstCycle_StoresObservationAsRevisionZero()
    {
        var summary = await RunAsync(_cycle1,
            new ParseResult([Record(333.952m)], [], 1));

        Assert.Equal(CollectionRunStatus.Succeeded, summary.Status);
        Assert.Equal(1, summary.Inserted);

        await using var verify = _fixture.CreateContext();
        var observation = await verify.Observations
            .SingleAsync(o => o.Series!.SeriesCode == _seriesCode);

        Assert.Equal(333.952m, observation.Value);
        Assert.Equal(0, observation.RevisionNumber);
        Assert.True(observation.IsCurrent);
        Assert.Null(observation.SupersededAtUtc);

        // FR-6: the collection timestamp, and the key SQL Server computes from the period.
        Assert.Equal(20260601, observation.ReferenceDateKey);
        Assert.Equal(_cycle1, observation.CollectedAtUtc);
    }

    [Fact]
    public async Task ReissuingTheSameValue_WritesNothing()
    {
        // FR-3. Polling monthly data hourly means this is the overwhelmingly common path.
        await RunAsync(_cycle1, new ParseResult([Record(333.952m)], [], 1));

        var summary = await RunAsync(_cycle2, new ParseResult([Record(333.952m)], [], 1));

        Assert.Equal(0, summary.Inserted);
        Assert.Equal(0, summary.Revised);
        Assert.Equal(1, summary.Unchanged);

        await using var verify = _fixture.CreateContext();
        Assert.Equal(1, await verify.Observations.CountAsync(o => o.Series!.SeriesCode == _seriesCode));
    }

    [Fact]
    public async Task ARevisedValue_SupersedesTheOldVintageAndKeepsIt()
    {
        // FR-4: history is retained, never overwritten.
        await RunAsync(_cycle1, new ParseResult([Record(333.952m)], [], 1));

        var summary = await RunAsync(_cycle2, new ParseResult([Record(334.100m, "R")], [], 1));

        Assert.Equal(1, summary.Revised);
        Assert.Equal(0, summary.Inserted);

        await using var verify = _fixture.CreateContext();
        var vintages = await verify.Observations
            .Where(o => o.Series!.SeriesCode == _seriesCode)
            .OrderBy(o => o.RevisionNumber)
            .ToListAsync();

        Assert.Equal(2, vintages.Count);

        Assert.Equal(333.952m, vintages[0].Value);
        Assert.False(vintages[0].IsCurrent);
        Assert.NotNull(vintages[0].SupersededAtUtc);

        Assert.Equal(334.100m, vintages[1].Value);
        Assert.Equal(1, vintages[1].RevisionNumber);
        Assert.True(vintages[1].IsCurrent);
        Assert.Equal("R", vintages[1].SourceAnnotation);
    }

    [Fact]
    public async Task ExactlyOneVintageStaysCurrentAcrossRepeatedRevisions()
    {
        // The integrity rule the dashboards depend on: UQ_Observation_Current would reject a
        // second live vintage outright, so this proves the supersede-then-append order holds.
        await RunAsync(_cycle1, new ParseResult([Record(1m)], [], 1));
        await RunAsync(_cycle2, new ParseResult([Record(2m)], [], 1));
        await RunAsync(_cycle2.AddHours(1), new ParseResult([Record(3m)], [], 1));

        await using var verify = _fixture.CreateContext();
        var live = await verify.Observations
            .Where(o => o.Series!.SeriesCode == _seriesCode && o.IsCurrent)
            .ToListAsync();

        Assert.Single(live);
        Assert.Equal(3m, live[0].Value);
        Assert.Equal(2, live[0].RevisionNumber);
    }

    [Fact]
    public async Task AnnotationOnlyChange_IsStillARevision()
    {
        // BLS flips a footnote to "R" without necessarily moving the number; that transition is
        // meaningful for economic data and must not be swallowed.
        await RunAsync(_cycle1, new ParseResult([Record(333.952m)], [], 1));

        var summary = await RunAsync(_cycle2, new ParseResult([Record(333.952m, "R")], [], 1));

        Assert.Equal(1, summary.Revised);
    }

    [Fact]
    public async Task UnknownSeries_IsRejectedRatherThanAutoCreated()
    {
        // Silently inventing series is how a publisher typo becomes permanent reference data.
        var stray = Record(1m) with { SeriesCode = "NOT_REGISTERED" };

        var summary = await RunAsync(_cycle1, new ParseResult([stray], [], 1));

        Assert.Equal(0, summary.Inserted);
        Assert.Equal(1, summary.Rejected);

        await using var verify = _fixture.CreateContext();
        var rejection = await verify.RejectedObservations
            .SingleAsync(r => r.CollectionRunId == summary.CollectionRunId);

        Assert.Equal(RejectionReason.UnknownSeries, rejection.Reason);
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
    public async Task PartialSuccess_WhenSomeRecordsAreRejected()
    {
        var rejection = new RejectedFragment(
            _seriesCode, "2026-13-01", RejectionReason.UnparseablePeriod, "Bad period.", "{}");

        var summary = await RunAsync(_cycle1,
            new ParseResult([Record(1m)], [rejection], 2));

        Assert.Equal(CollectionRunStatus.PartialSuccess, summary.Status);
        Assert.Equal(1, summary.Inserted);
        Assert.Equal(1, summary.Rejected);
    }

    [Fact]
    public async Task AnIdenticalPayload_ShortCircuitsBeforeParsing()
    {
        // The cheapest possible dedup: byte-identical bodies are recognised from the stored
        // payload hash, so no observation is even parsed. Scoped to this test's series so it
        // cannot collide with another test's stored payload.
        var body = $$"""{"series":"{{_seriesCode}}","effectiveDate":"2026-07-31"}""";

        await RunAsync(_cycle1, new ParseResult([Record(1m)], [], 1), payload: body);

        // The adapter would return a different value, but it is never consulted.
        var summary = await RunAsync(_cycle2, new ParseResult([Record(999m)], [], 1), payload: body);

        Assert.Equal(CollectionRunStatus.Succeeded, summary.Status);
        Assert.Equal(0, summary.Fetched);

        await using var verify = _fixture.CreateContext();
        var values = await verify.Observations
            .Where(o => o.Series!.SeriesCode == _seriesCode)
            .Select(o => o.Value)
            .ToListAsync();

        Assert.Equal([1m], values);
    }

    [Fact]
    public async Task RawPayloadIsStoredForDiagnosis()
    {
        var summary = await RunAsync(_cycle1, new ParseResult([Record(1m)], [], 1));

        await using var verify = _fixture.CreateContext();
        var payload = await verify.RawPayloads.SingleAsync(p => p.CollectionRunId == summary.CollectionRunId);

        Assert.Equal(32, payload.ContentHash.Length);
        Assert.True(payload.SizeBytes > 0);
        Assert.True(payload.CompressedContent.Length > 0);
    }

    [Fact]
    public async Task RetryOfTheSameCycle_GetsADistinctAttemptNumber()
    {
        await RunAsync(_cycle1, new ParseResult([Record(1m)], [], 1));
        await RunAsync(_cycle1, new ParseResult([Record(1m)], [], 1), trigger: CollectionTriggerType.Retry);

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
        await using var db = _fixture.CreateContext();

        var runner = new CollectionRunner(
            db,
            new StubFetcher("{}", null),
            [new StubAdapter(new ParseResult([], [], 0))],
            new AllowAllRobotsPolicy(),
            Options.Create(new CollectionOptions()),
            new FixedTimeProvider(_cycle1),
            NullLogger<CollectionRunner>.Instance);

        var summary = await runner.RunAsync(
            "NO_SUCH_SOURCE", _cycle1, CollectionTriggerType.Scheduled, CancellationToken.None);

        Assert.Equal(CollectionRunStatus.Skipped, summary.Status);
        Assert.Null(summary.CollectionRunId);
    }

    private sealed class StubFetcher(string content, FetchResult? forcedFailure) : ISourceFetcher
    {
        public Task<FetchResult> FetchAsync(
            SourceRequest request, DataSource source, CancellationToken cancellationToken) =>
            Task.FromResult(forcedFailure ?? FetchResult.Success(content, "application/json", 200, 1));
    }

    private sealed class StubAdapter(ParseResult result) : ISourceAdapter
    {
        public string SourceCode => DataSource.NyFedSofrCode;

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
