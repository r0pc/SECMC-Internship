using System.ComponentModel.DataAnnotations;

namespace DataIntelligence.Infrastructure.Collection;

/// <summary>
/// Everything about how the platform collects, bound from the <c>Collection</c> configuration
/// section and validated at startup.
/// </summary>
/// <remarks>
/// Configuration rather than code because the source is still <c>[DATA SOURCE — TBD]</c>
/// (SOW 0.1): confirming it, or repairing a selector after the site's markup shifts, is an
/// edit and a restart instead of a code change and a redeploy (SOW 9, Risk 1).
/// </remarks>
public sealed class CollectionOptions
{
    public const string SectionName = "Collection";

    /// <summary>Display name for the source, recorded in <c>collect.SourceConfig</c>.</summary>
    public string SourceName { get; set; } = "[DATA SOURCE - TBD]";

    /// <summary>
    /// The URL to collect. Empty until the source is signed off — the Worker then logs and
    /// idles rather than failing, so the service is deployable before that gate clears.
    /// </summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>Minutes between cycles. 60 satisfies FR-1's hourly requirement.</summary>
    [Range(1, 1440)]
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Align cycles to the wall clock, so an hourly schedule fires at :00 rather than at
    /// whatever minute the service happened to start. Makes run times predictable for the
    /// operations team and comparable across restarts.
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
    /// Identifies the collector to the source operator, per common crawling etiquette, and is
    /// the token matched against robots.txt user-agent groups.
    /// </summary>
    public string UserAgent { get; set; } = "DataIntelligencePlatform/1.0";

    /// <summary>
    /// Enforce robots.txt before fetching (SOW 3 — Compliance). Leave on. It exists as a switch
    /// only for collecting from a source the sponsor owns, where robots.txt is irrelevant.
    /// </summary>
    public bool RespectRobotsTxt { get; set; } = true;

    /// <summary>How long a fetched robots.txt is reused before being re-fetched.</summary>
    [Range(1, 1440)]
    public int RobotsCacheMinutes { get; set; } = 60;

    /// <summary>
    /// Store a snapshot every cycle even when nothing changed. Off by default: an unchanged
    /// observation is reconstructable from the item's <c>LastSeenAtUtc</c> and the previous
    /// snapshot, so skipping it keeps the fact table small without losing history (FR-3, FR-4).
    /// </summary>
    public bool StoreUnchangedSnapshots { get; set; }

    /// <summary>
    /// Keep the compressed response body for each run. Diagnostic value is high while selectors
    /// are being tuned; it is also the bulk of the storage, so it is purgeable independently.
    /// </summary>
    public bool StoreRawPayload { get; set; } = true;

    /// <summary>
    /// Refuse a payload larger than this. A source that suddenly returns something enormous is
    /// far more likely to be an error page or a redirect loop than real data.
    /// </summary>
    [Range(1, 512)]
    public int MaxPayloadMegabytes { get; set; } = 16;

    /// <summary>
    /// Mark items not seen for this many cycles as inactive. Guards against a single bad parse
    /// retiring the whole catalogue.
    /// </summary>
    [Range(1, 168)]
    public int InactiveAfterMissedCycles { get; set; } = 24;

    [Required]
    public ParserOptions Parser { get; set; } = new();
}
