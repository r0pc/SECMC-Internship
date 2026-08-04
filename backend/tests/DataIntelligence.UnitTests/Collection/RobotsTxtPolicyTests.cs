using System.Net;
using System.Text;
using DataIntelligence.Infrastructure.Collection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataIntelligence.UnitTests.Collection;

/// <summary>
/// robots.txt enforcement (SOW 3 — Compliance). Driven through the public policy with a stub
/// handler, so the tests cover the same path production takes.
/// </summary>
public class RobotsTxtPolicyTests
{
    private const string TargetUrl = "https://example.test/data/listings";

    private static RobotsTxtPolicy CreatePolicy(
        string? robotsBody,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        Exception? throwInstead = null,
        string userAgent = "DataIntelligencePlatform/1.0")
    {
        var handler = new StubHandler(robotsBody, statusCode, throwInstead);
        var options = Options.Create(new CollectionOptions
        {
            UserAgent = userAgent,
            RespectRobotsTxtForHtmlSources = true
        });

        return new RobotsTxtPolicy(
            new HttpClient(handler),
            new MemoryCache(new MemoryCacheOptions()),
            options,
            NullLogger<RobotsTxtPolicy>.Instance);
    }

    [Fact]
    public async Task AllowsWhenNoRuleMatches()
    {
        var policy = CreatePolicy("User-agent: *\nDisallow: /admin/");

        var decision = await policy.EvaluateAsync(TargetUrl, CancellationToken.None);

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task DisallowsAMatchingPrefix()
    {
        var policy = CreatePolicy("User-agent: *\nDisallow: /data/");

        var decision = await policy.EvaluateAsync(TargetUrl, CancellationToken.None);

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public async Task LongestMatchWins()
    {
        // RFC 9309 §2.2.2: the more specific Allow beats the broader Disallow.
        var policy = CreatePolicy("User-agent: *\nDisallow: /data/\nAllow: /data/listings");

        var decision = await policy.EvaluateAsync(TargetUrl, CancellationToken.None);

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task EmptyDisallowMeansAllowEverything()
    {
        var policy = CreatePolicy("User-agent: *\nDisallow:");

        var decision = await policy.EvaluateAsync(TargetUrl, CancellationToken.None);

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task OurOwnGroupOverridesTheWildcardGroup()
    {
        // A crawler must obey its own group and ignore '*' entirely when one exists.
        var policy = CreatePolicy(
            "User-agent: *\nDisallow: /\n\nUser-agent: DataIntelligencePlatform\nAllow: /data/");

        var decision = await policy.EvaluateAsync(TargetUrl, CancellationToken.None);

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task WildcardGroupAppliesWhenThereIsNoSpecificGroup()
    {
        var policy = CreatePolicy("User-agent: SomeOtherBot\nAllow: /\n\nUser-agent: *\nDisallow: /data/");

        var decision = await policy.EvaluateAsync(TargetUrl, CancellationToken.None);

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public async Task SupportsWildcardsInPaths()
    {
        var policy = CreatePolicy("User-agent: *\nDisallow: /*/listings");

        var decision = await policy.EvaluateAsync(TargetUrl, CancellationToken.None);

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public async Task SupportsEndAnchors()
    {
        // '/data/listings$' matches this path exactly, so it is disallowed...
        var exact = await CreatePolicy("User-agent: *\nDisallow: /data/listings$")
            .EvaluateAsync(TargetUrl, CancellationToken.None);
        Assert.False(exact.IsAllowed);

        // ...but the anchor means a longer path is not covered.
        var longer = await CreatePolicy("User-agent: *\nDisallow: /data/listings$")
            .EvaluateAsync(TargetUrl + "/page2", CancellationToken.None);
        Assert.True(longer.IsAllowed);
    }

    [Fact]
    public async Task IgnoresComments()
    {
        var policy = CreatePolicy("User-agent: *   # everyone\nDisallow: /data/  # the good stuff");

        var decision = await policy.EvaluateAsync(TargetUrl, CancellationToken.None);

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public async Task ReadsCrawlDelay()
    {
        var policy = CreatePolicy("User-agent: *\nCrawl-delay: 10\nDisallow: /admin/");

        var decision = await policy.EvaluateAsync(TargetUrl, CancellationToken.None);

        Assert.True(decision.IsAllowed);
        Assert.Equal(TimeSpan.FromSeconds(10), decision.CrawlDelay);
    }

    [Fact]
    public async Task MissingRobotsFileMeansUnrestricted()
    {
        // 4xx is "nothing published", which RFC 9309 treats as full allowance.
        var policy = CreatePolicy(null, HttpStatusCode.NotFound);

        var decision = await policy.EvaluateAsync(TargetUrl, CancellationToken.None);

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task ServerErrorFailsClosed()
    {
        // Fail-closed: a wrongly skipped run is recoverable, a wrongly scraped one is not.
        var policy = CreatePolicy(null, HttpStatusCode.InternalServerError);

        var decision = await policy.EvaluateAsync(TargetUrl, CancellationToken.None);

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public async Task UnreachableRobotsFileFailsClosed()
    {
        var policy = CreatePolicy(null, throwInstead: new HttpRequestException("connection refused"));

        var decision = await policy.EvaluateAsync(TargetUrl, CancellationToken.None);

        Assert.False(decision.IsAllowed);
    }

    [Fact]
    public async Task EnforcementCanBeDisabledForASponsorOwnedSource()
    {
        var handler = new StubHandler("User-agent: *\nDisallow: /", HttpStatusCode.OK, null);
        var policy = new RobotsTxtPolicy(
            new HttpClient(handler),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new CollectionOptions { RespectRobotsTxtForHtmlSources = false }),
            NullLogger<RobotsTxtPolicy>.Instance);

        var decision = await policy.EvaluateAsync(TargetUrl, CancellationToken.None);

        Assert.True(decision.IsAllowed);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task RobotsFileIsFetchedOncePerHost()
    {
        var handler = new StubHandler("User-agent: *\nDisallow: /admin/", HttpStatusCode.OK, null);
        var policy = new RobotsTxtPolicy(
            new HttpClient(handler),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new CollectionOptions { RespectRobotsTxtForHtmlSources = true, RobotsCacheMinutes = 60 }),
            NullLogger<RobotsTxtPolicy>.Instance);

        await policy.EvaluateAsync(TargetUrl, CancellationToken.None);
        await policy.EvaluateAsync(TargetUrl, CancellationToken.None);
        await policy.EvaluateAsync(TargetUrl, CancellationToken.None);

        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    // A bare path. Unix parses this as an absolute file URI, so an absolute-only check passes
    // on Windows and lets it through on Linux — this test exists to pin that difference.
    [InlineData("/data/listings")]
    [InlineData("data/listings")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.test/data")]
    [InlineData("")]
    public async Task RejectsAnythingThatIsNotAnAbsoluteHttpUrl(string url)
    {
        var policy = CreatePolicy("User-agent: *\nDisallow:");

        var decision = await policy.EvaluateAsync(url, CancellationToken.None);

        Assert.False(decision.IsAllowed);
    }

    [Theory]
    [InlineData("http://example.test/data")]
    [InlineData("https://example.test/data")]
    public async Task AcceptsAbsoluteHttpUrls(string url)
    {
        var policy = CreatePolicy("User-agent: *\nDisallow:");

        var decision = await policy.EvaluateAsync(url, CancellationToken.None);

        Assert.True(decision.IsAllowed);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string? _body;
        private readonly HttpStatusCode _statusCode;
        private readonly Exception? _throwInstead;

        public StubHandler(string? body, HttpStatusCode statusCode, Exception? throwInstead)
        {
            _body = body;
            _statusCode = statusCode;
            _throwInstead = throwInstead;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;

            if (_throwInstead is not null)
            {
                return Task.FromException<HttpResponseMessage>(_throwInstead);
            }

            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body ?? string.Empty, Encoding.UTF8, "text/plain")
            });
        }
    }
}
