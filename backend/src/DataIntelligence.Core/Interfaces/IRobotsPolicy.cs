namespace DataIntelligence.Core.Interfaces;

/// <summary>
/// Enforces the source site's robots.txt directives before any collection request
/// (SOW 3 — Compliance).
/// </summary>
public interface IRobotsPolicy
{
    /// <summary>
    /// Whether <paramref name="url"/> may be fetched. Implementations cache the source's
    /// robots.txt rather than re-fetching it every cycle.
    /// </summary>
    Task<RobotsDecision> EvaluateAsync(string url, CancellationToken cancellationToken);
}

/// <param name="IsAllowed">False means the run must be skipped, not failed.</param>
/// <param name="Reason">Human-readable explanation, recorded on the run when disallowed.</param>
/// <param name="CrawlDelay">
/// The source's requested minimum gap between requests, where it publishes one. Honoured even
/// though hourly collection is far slower than any realistic crawl-delay.
/// </param>
public sealed record RobotsDecision(bool IsAllowed, string Reason, TimeSpan? CrawlDelay = null)
{
    public static RobotsDecision Allowed(string reason, TimeSpan? crawlDelay = null) =>
        new(true, reason, crawlDelay);

    public static RobotsDecision Disallowed(string reason) => new(false, reason);
}
