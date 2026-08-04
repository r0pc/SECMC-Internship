using DataIntelligence.Core.Dtos;
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
/// tests are deterministic and reach no external site; everything below it is production code.
/// </summary>
/// <remarks>
/// The class shares one database, so each test gets its own source key and its own schedule
/// slot — otherwise tests would collide on <c>UQ_Item_SourceKey</c> and on the attempt numbering
/// behind <c>UQ_CollectionRun_Cycle</c>. Migrating a database per test would be cleaner still,
/// but costs seconds per test for isolation that unique keys already provide.
/// </remarks>
public class CollectionRunnerTests : IClassFixture<CollectionDatabaseFixture>
{
    private const string RecordSelector = "//div[@class='listing']";

    /// <summary>Hands each test instance a private block of schedule slots.</summary>
    private static int _slotCounter;

    private readonly CollectionDatabaseFixture _fixture;
    private readonly string _sourceKey;
    private readonly DateTime _cycle1;
    private readonly DateTime _cycle2;

    public CollectionRunnerTests(CollectionDatabaseFixture fixture)
    {
        _fixture = fixture;

        // These tests require a real SQL Server, which SOW 11.2 provisions for exactly this
        // purpose. Failing with an actionable message beats passing silently and reporting
        // coverage the run never actually had.
        if (!fixture.IsAvailable)
        {
            throw new InvalidOperationException(fixture.UnavailableReason);
        }

        var slot = Interlocked.Increment(ref _slotCounter);
        _sourceKey = $"ITEM-{slot:D4}";
        _cycle1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(slot * 24);
        _cycle2 = _cycle1.AddHours(1);
    }

    private string Page(string price, string quantity = "5", string title = "First item") => $"""
        <html><body>
          <div class="listing" data-id="{_sourceKey}">
            <h3>{title}</h3><span class="price">{price}</span><span class="stock">{quantity}</span>
          </div>
        </body></html>
        """;

    private static IOptions<CollectionOptions> CreateOptions(bool storeUnchanged = false) =>
        Options.Create(new CollectionOptions
        {
            SourceUrl = "https://example.test/data",
            StoreUnchangedSnapshots = storeUnchanged,
            StoreRawPayload = true,
            Parser = new ParserOptions
            {
                RecordSelector = RecordSelector,
                Fields = new Dictionary<string, FieldSelector>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SourceKey"] = new() { Selector = ".", Attribute = "data-id", Required = true },
                    ["Title"] = new() { Selector = ".//h3", Required = true },
                    ["PrimaryValue"] = new() { Selector = ".//span[@class='price']", Type = FieldType.Decimal },
                    ["Quantity"] = new() { Selector = ".//span[@class='stock']", Type = FieldType.Integer }
                }
            }
        });

    private (CollectionRunner Runner, DataIntelligenceDbContext Db) CreateRunner(
        string content,
        DateTime now,
        bool storeUnchanged = false,
        FetchResult? forcedFailure = null,
        IRobotsPolicy? robotsPolicy = null)
    {
        var options = CreateOptions(storeUnchanged);
        var db = _fixture.CreateContext();

        var runner = new CollectionRunner(
            db,
            new StubFetcher(content, forcedFailure),
            new SelectorHtmlParser(options, NullLogger<SelectorHtmlParser>.Instance),
            robotsPolicy ?? new AllowAllRobotsPolicy(),
            options,
            new FixedTimeProvider(now),
            NullLogger<CollectionRunner>.Instance);

        return (runner, db);
    }

    /// <summary>Runs one cycle in its own context, as the Worker does with a scope per cycle.</summary>
    private async Task<CollectionSummary> RunCycleAsync(
        string content,
        DateTime scheduledFor,
        bool storeUnchanged = false,
        FetchResult? forcedFailure = null,
        IRobotsPolicy? robotsPolicy = null,
        CollectionTriggerType trigger = CollectionTriggerType.Scheduled)
    {
        var (runner, db) = CreateRunner(content, scheduledFor, storeUnchanged, forcedFailure, robotsPolicy);
        await using (db)
        {
            return await runner.RunAsync(scheduledFor, trigger, CancellationToken.None);
        }
    }

    [Fact]
    public async Task FirstCycle_StoresTheItemAndItsSnapshot()
    {
        var summary = await RunCycleAsync(Page("19.99"), _cycle1);

        Assert.Equal(CollectionRunStatus.Succeeded, summary.Status);
        Assert.Equal(1, summary.RecordsInserted);

        await using var verify = _fixture.CreateContext();
        var item = await verify.Items.SingleAsync(i => i.SourceKey == _sourceKey);
        var snapshot = await verify.ItemSnapshots.SingleAsync(s => s.ItemId == item.ItemId);

        Assert.Equal(19.99m, snapshot.PrimaryValue);
        Assert.Equal(5, snapshot.Quantity);

        // FR-6: the collection timestamp, and the date key SQL Server computes from it.
        Assert.Equal(_cycle1, snapshot.CollectedAtUtc);
        Assert.Equal(int.Parse(_cycle1.ToString("yyyyMMdd")), snapshot.CollectedDateKey);
    }

    [Fact]
    public async Task RerunningTheSameCycle_DoesNotDuplicateRows()
    {
        // FR-3, stated literally: re-running a collection cycle creates no duplicate rows.
        await RunCycleAsync(Page("42.00"), _cycle1);

        var summary = await RunCycleAsync(
            Page("42.00"), _cycle1, trigger: CollectionTriggerType.Retry);

        Assert.Equal(0, summary.RecordsInserted);
        Assert.Equal(1, summary.RecordsUnchanged);

        await using var verify = _fixture.CreateContext();
        var item = await verify.Items.SingleAsync(i => i.SourceKey == _sourceKey);

        Assert.Equal(1, await verify.ItemSnapshots.CountAsync(s => s.ItemId == item.ItemId));
    }

    [Fact]
    public async Task ChangedValue_AppendsASnapshotAndKeepsTheOriginal()
    {
        // FR-4: history is retained, never overwritten.
        await RunCycleAsync(Page("10.00"), _cycle1);

        var summary = await RunCycleAsync(Page("12.50"), _cycle2);
        Assert.Equal(1, summary.RecordsInserted);

        await using var verify = _fixture.CreateContext();
        var item = await verify.Items.SingleAsync(i => i.SourceKey == _sourceKey);
        var values = await verify.ItemSnapshots
            .Where(s => s.ItemId == item.ItemId)
            .OrderBy(s => s.CollectedAtUtc)
            .Select(s => s.PrimaryValue)
            .ToListAsync();

        Assert.Equal([10.00m, 12.50m], values);
    }

    [Fact]
    public async Task UnchangedItem_StillUpdatesLastSeen()
    {
        // What makes skipping an unchanged snapshot lossless: presence is still recorded.
        await RunCycleAsync(Page("7.00"), _cycle1);
        await RunCycleAsync(Page("7.00"), _cycle2);

        await using var verify = _fixture.CreateContext();
        var item = await verify.Items.SingleAsync(i => i.SourceKey == _sourceKey);

        Assert.Equal(_cycle1, item.FirstSeenAtUtc);
        Assert.Equal(_cycle2, item.LastSeenAtUtc);
        Assert.Equal(1, await verify.ItemSnapshots.CountAsync(s => s.ItemId == item.ItemId));
    }

    [Fact]
    public async Task StoreUnchangedSnapshots_WritesARowPerCycleWithHasChangedFalse()
    {
        await RunCycleAsync(Page("3.00"), _cycle1, storeUnchanged: true);
        await RunCycleAsync(Page("3.00"), _cycle2, storeUnchanged: true);

        await using var verify = _fixture.CreateContext();
        var item = await verify.Items.SingleAsync(i => i.SourceKey == _sourceKey);
        var snapshots = await verify.ItemSnapshots
            .Where(s => s.ItemId == item.ItemId)
            .OrderBy(s => s.CollectedAtUtc)
            .ToListAsync();

        Assert.Equal(2, snapshots.Count);
        Assert.True(snapshots[0].HasChanged);
        Assert.False(snapshots[1].HasChanged);
    }

    [Fact]
    public async Task FetchFailure_IsRecordedAndCategorised()
    {
        // FR-2: the failure is logged with a category, and nothing throws.
        var failure = FetchResult.Failure(
            CollectionFailureCategory.Timeout, "Request exceeded the 30s timeout.");

        var summary = await RunCycleAsync(Page("1.00"), _cycle1, forcedFailure: failure);

        Assert.Equal(CollectionRunStatus.Failed, summary.Status);
        Assert.Equal(CollectionFailureCategory.Timeout, summary.FailureCategory);
        Assert.True(summary.ShouldRetry);

        await using var verify = _fixture.CreateContext();
        var run = await verify.CollectionRuns.SingleAsync(r => r.CollectionRunId == summary.CollectionRunId);

        Assert.Equal(CollectionRunStatus.Failed, run.Status);
        Assert.Equal(CollectionFailureCategory.Timeout, run.FailureCategory);
        Assert.NotNull(run.CompletedAtUtc);
    }

    [Fact]
    public async Task SelectorMatchingNothing_IsReportedAsALayoutChange()
    {
        // Distinguishes "the site changed" from "the site is down" — different fix, different alert.
        var summary = await RunCycleAsync("<html><body><p>redesigned</p></body></html>", _cycle1);

        Assert.Equal(CollectionRunStatus.Failed, summary.Status);
        Assert.Equal(CollectionFailureCategory.LayoutChanged, summary.FailureCategory);

        // Not retried: another request returns the same redesigned page.
        Assert.False(summary.ShouldRetry);
    }

    [Fact]
    public async Task InvalidRecord_IsRejectedWithAReasonAndTheRunIsPartial()
    {
        var html = $"""
            <html><body>
              <div class="listing" data-id="{_sourceKey}"><h3>Fine</h3><span class="price">1.00</span><span class="stock">1</span></div>
              <div class="listing" data-id="{_sourceKey}-BAD"><h3>Broken</h3><span class="price">1.00</span><span class="stock">-4</span></div>
            </body></html>
            """;

        var summary = await RunCycleAsync(html, _cycle1);

        Assert.Equal(CollectionRunStatus.PartialSuccess, summary.Status);
        Assert.Equal(1, summary.RecordsInserted);
        Assert.Equal(1, summary.RecordsRejected);

        await using var verify = _fixture.CreateContext();
        var rejection = await verify.RejectedRecords
            .SingleAsync(r => r.CollectionRunId == summary.CollectionRunId);

        Assert.Equal(RejectionReason.OutOfRange, rejection.Reason);
        Assert.Equal($"{_sourceKey}-BAD", rejection.SourceKey);
    }

    [Fact]
    public async Task RobotsDisallow_SkipsTheCycleInsteadOfFailingIt()
    {
        // A deliberate, correct decision must not count against the reliability metric.
        var summary = await RunCycleAsync(
            Page("1.00"), _cycle1, robotsPolicy: new DenyAllRobotsPolicy());

        Assert.Equal(CollectionRunStatus.Skipped, summary.Status);
        Assert.Null(summary.FailureCategory);
        Assert.False(summary.ShouldRetry);
    }

    [Fact]
    public async Task RawPayloadIsStoredForDiagnosis()
    {
        var summary = await RunCycleAsync(Page("55.00"), _cycle1);

        await using var verify = _fixture.CreateContext();
        var payload = await verify.RawPayloads.SingleAsync(p => p.CollectionRunId == summary.CollectionRunId);

        Assert.Equal(32, payload.ContentHash.Length);
        Assert.True(payload.SizeBytes > 0);
        Assert.True(payload.CompressedContent.Length > 0);
    }

    [Fact]
    public async Task RetryOfTheSameCycle_GetsADistinctAttemptNumber()
    {
        // UQ_CollectionRun_Cycle is (ScheduledForUtc, Attempt); a retry must not collide with
        // the run it is retrying.
        await RunCycleAsync(Page("2.00"), _cycle1);
        await RunCycleAsync(Page("2.00"), _cycle1, trigger: CollectionTriggerType.Retry);

        await using var verify = _fixture.CreateContext();
        var attempts = await verify.CollectionRuns
            .Where(r => r.ScheduledForUtc == _cycle1)
            .Select(r => r.Attempt)
            .OrderBy(a => a)
            .ToListAsync();

        Assert.Equal([(byte)1, (byte)2], attempts);
    }

    private sealed class StubFetcher : ISourceFetcher
    {
        private readonly string _content;
        private readonly FetchResult? _forcedFailure;

        public StubFetcher(string content, FetchResult? forcedFailure)
        {
            _content = content;
            _forcedFailure = forcedFailure;
        }

        public Task<FetchResult> FetchAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult(_forcedFailure ?? FetchResult.Success(_content, "text/html", 200, 1));
    }

    private sealed class AllowAllRobotsPolicy : IRobotsPolicy
    {
        public Task<RobotsDecision> EvaluateAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult(RobotsDecision.Allowed("Test policy."));
    }

    private sealed class DenyAllRobotsPolicy : IRobotsPolicy
    {
        public Task<RobotsDecision> EvaluateAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult(RobotsDecision.Disallowed("Disallowed by test policy."));
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTime utcNow) => _now = new DateTimeOffset(utcNow);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
