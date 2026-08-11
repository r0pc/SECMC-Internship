using System.ComponentModel.DataAnnotations;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Dtos;

/// <summary>A publisher the platform collects from (FR-7).</summary>
public sealed record DataSourceDto
{
    public required byte DataSourceId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Publisher { get; init; }
    public required string LandingPageUrl { get; init; }
    public required SourceAccessMethod AccessMethod { get; init; }

    /// <summary>How often the publisher releases — not how often the platform polls.</summary>
    public required string PublicationCadence { get; init; }

    public required short CollectionIntervalMinutes { get; init; }
    public required short RequestTimeoutSec { get; init; }
    public required byte MaxRetries { get; init; }
    public string? UserAgent { get; init; }

    /// <summary>Compliance evidence for the dashboard footer (SOW 3 — Compliance).</summary>
    public string? TermsOfUseUrl { get; init; }

    public required bool RequiresApiKey { get; init; }
    public required bool IsEnabled { get; init; }

    /// <summary>Chartable series this source provides. Fixed by the catalogue, not by rows.</summary>
    public required int SeriesCount { get; init; }
}

/// <summary>
/// The mutable part of a source. Identity, endpoint, and access method are deliberately absent:
/// they are pinned to the adapter compiled against that publisher's contract, so editing them
/// through the API could only ever break collection.
/// </summary>
public sealed record DataSourceUpdateRequest
{
    public bool? IsEnabled { get; init; }

    [Range(1, 1440)]
    public short? CollectionIntervalMinutes { get; init; }

    [Range(1, 300)]
    public short? RequestTimeoutSec { get; init; }

    [Range(0, 10)]
    public byte? MaxRetries { get; init; }

    [MaxLength(250)]
    public string? UserAgent { get; init; }

    [MaxLength(500)]
    [Url]
    public string? TermsOfUseUrl { get; init; }
}

/// <summary>
/// One chartable measure, with enough context to render it correctly.
/// </summary>
/// <remarks>
/// Read-only, and not a database row. The two datasets each have their own table, so what a chart
/// may draw is a fixed list in code rather than a registry that could be edited into disagreement
/// with the collector — which is also why there is no update request type here any more.
/// <para>
/// <see cref="Unit"/> and <see cref="DecimalPlaces"/> are not decoration. Values are stored
/// exactly as published and never rescaled, so a chart that ignores the unit will plot SOFR
/// volume in billions against CPI index points on one axis and look fine while being nonsense.
/// </para>
/// </remarks>
public sealed record SeriesDto
{
    /// <summary>Stable identifier used in URLs and saved dashboard views: <c>cpi</c>, <c>sofr.p25</c>.</summary>
    public required string SeriesKey { get; init; }

    /// <summary>Which table the values come from.</summary>
    public required Dataset Dataset { get; init; }

    public required byte DataSourceId { get; init; }
    public required string SourceCode { get; init; }

    /// <summary>
    /// The publisher's own identifier — the BLS series id, or the NY Fed rate type. What to quote
    /// when asking the publisher about a number.
    /// </summary>
    public required string PublisherCode { get; init; }

    public required string Title { get; init; }

    /// <summary>Verbatim from the publisher. Axis labels must use it.</summary>
    public required string Unit { get; init; }

    public required byte DecimalPlaces { get; init; }
    public required SeriesFrequency Frequency { get; init; }
    public required SeasonalAdjustment SeasonalAdjustment { get; init; }

    public required string SourceUrl { get; init; }

    /// <summary>Most recent current value, or null when nothing has been collected yet.</summary>
    public SeriesLatestPointDto? Latest { get; init; }
}

/// <summary>The newest value for a series, for a picker or a KPI tile.</summary>
public sealed record SeriesLatestPointDto
{
    public required DateOnly ReferenceDate { get; init; }
    public required decimal Value { get; init; }

    /// <summary>When the platform learned this value (FR-6), not when it was published.</summary>
    public required DateTime CollectedAtPkt { get; init; }
}
