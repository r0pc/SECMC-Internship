using System.ComponentModel.DataAnnotations;

namespace DataIntelligence.Infrastructure.Collection;

/// <summary>
/// How the platform collects, bound from the <c>Collection</c> configuration section and
/// validated at startup.
/// </summary>
public sealed class CollectionOptions
{
    public const string SectionName = "Collection";

    /// <summary>Minutes between cycles. 60 satisfies FR-1's hourly requirement.</summary>
    [Range(1, 1440)]
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Align cycles to the wall clock, so an hourly schedule fires at :00 rather than at
    /// whatever minute the service happened to start. Keeps run times predictable across
    /// restarts and comparable between the two sources.
    /// </summary>
    public bool AlignToClock { get; set; } = true;

    [Range(1, 300)]
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Retries *after* the first attempt, so 3 means up to 4 requests.</summary>
    [Range(0, 10)]
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base for exponential backoff between retries: 2s, 4s, 8s.</summary>
    [Range(1, 60)]
    public int RetryBaseDelaySeconds { get; set; } = 2;

    /// <summary>
    /// Identifies the collector to the publisher, per common etiquette for automated clients.
    /// </summary>
    public string UserAgent { get; set; } = "DataIntelligencePlatform/1.0";

    /// <summary>
    /// Enforce robots.txt for sources retrieved as HTML.
    /// </summary>
    /// <remarks>
    /// Deliberately scoped to HTML. RFC 9309 governs crawlers of web content; both confirmed
    /// sources are official JSON APIs published for programmatic use and carry their own terms,
    /// recorded in <c>collect.DataSource.TermsOfUseUrl</c>. Applying crawler directives to a
    /// documented API would be a category error — and would let an unrelated <c>Disallow</c>
    /// silently stop a sanctioned integration.
    /// </remarks>
    public bool RespectRobotsTxtForHtmlSources { get; set; } = true;

    [Range(1, 1440)]
    public int RobotsCacheMinutes { get; set; } = 60;

    /// <summary>
    /// Keep the compressed response for each run. Diagnostic value is high while adapters are
    /// being tuned, and it lets a cycle be re-parsed without spending BLS query budget.
    /// </summary>
    public bool StoreRawPayload { get; set; } = true;

    /// <summary>
    /// Refuse a payload larger than this. A publisher that suddenly returns something enormous
    /// is likelier to be serving an error page than real data.
    /// </summary>
    [Range(1, 512)]
    public int MaxPayloadMegabytes { get; set; } = 16;

    [Required]
    public BlsOptions Bls { get; set; } = new();
}

/// <summary>Settings for the BLS Consumer Price Index source.</summary>
public sealed class BlsOptions
{
    /// <summary>
    /// BLS registration key. Supplied through user secrets or an environment variable and never
    /// committed (SOW 3 — Security).
    /// </summary>
    /// <remarks>
    /// Optional by design. Unregistered v2 calls still succeed under a much smaller daily quota,
    /// so a missing key degrades the service rather than stopping it — and the degradation is
    /// visible as a <c>RateLimited</c> failure rather than as silence.
    /// </remarks>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Years of history to request each cycle. Two covers the year-over-year comparison the CPI
    /// dashboards need, plus room for a restatement of a recent month.
    /// </summary>
    /// <remarks>
    /// The full published history reaches back to 1913 and the API caps a request at 20 years, so
    /// a backfill is several requests with <c>TriggerType = Backfill</c> rather than a larger
    /// value here — the day-to-day cycle has no reason to re-request a century of settled figures.
    /// </remarks>
    [Range(1, 20)]
    public int YearsOfHistory { get; set; } = 2;

    /// <summary>
    /// The API's own cap on the span of a single request, which a backfill chunks against.
    /// </summary>
    /// <remarks>
    /// 20 years for a registered caller. An unregistered one is capped at 10, so lower this
    /// alongside leaving <see cref="ApiKey"/> unset — the API rejects an over-long range outright
    /// rather than truncating it, which would fail the whole backfill on its first chunk.
    /// </remarks>
    [Range(1, 20)]
    public int MaxYearsPerRequest { get; set; } = 20;
}
