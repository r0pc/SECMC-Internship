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

    /// <summary>Active series belonging to this source.</summary>
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

/// <summary>A grouping used for dashboard drill-down (FR-11).</summary>
public sealed record SeriesCategoryDto
{
    public required int CategoryId { get; init; }
    public int? ParentCategoryId { get; init; }
    public required string Code { get; init; }
    public required string DisplayName { get; init; }
    public required short SortOrder { get; init; }

    /// <summary>Series directly in this category, not counting descendants.</summary>
    public required int SeriesCount { get; init; }

    public required DateTime CreatedAtUtc { get; init; }
}

public sealed record SeriesCategoryCreateRequest
{
    [Required]
    [MaxLength(100)]
    public required string Code { get; init; }

    [Required]
    [MaxLength(200)]
    public required string DisplayName { get; init; }

    public int? ParentCategoryId { get; init; }

    public short SortOrder { get; init; }
}

public sealed record SeriesCategoryUpdateRequest
{
    [Required]
    [MaxLength(200)]
    public required string DisplayName { get; init; }

    public int? ParentCategoryId { get; init; }

    public short SortOrder { get; init; }
}

/// <summary>
/// One measured quantity, with enough context to render it correctly.
/// </summary>
/// <remarks>
/// <see cref="Unit"/> and <see cref="DecimalPlaces"/> are not decoration. Values are stored
/// exactly as published and never rescaled, so a chart that ignores the unit will plot SOFR
/// volume in billions against CPI index points on one axis and look fine while being nonsense.
/// </remarks>
public sealed record SeriesDto
{
    public required int SeriesId { get; init; }
    public required byte DataSourceId { get; init; }
    public required string SourceCode { get; init; }

    /// <summary>The publisher's identifier, or one assigned here — see <see cref="IsSourceAssignedCode"/>.</summary>
    public required string SeriesCode { get; init; }

    /// <summary>False when this platform invented the code, so nobody looks it up upstream.</summary>
    public required bool IsSourceAssignedCode { get; init; }

    public required string Title { get; init; }
    public int? CategoryId { get; init; }
    public string? CategoryName { get; init; }

    /// <summary>Verbatim from the publisher. Axis labels must use it.</summary>
    public required string Unit { get; init; }

    public byte? DecimalPlaces { get; init; }
    public required SeriesFrequency Frequency { get; init; }
    public required SeasonalAdjustment SeasonalAdjustment { get; init; }

    /// <summary>
    /// The period length of this series' regular releases. Every chart and aggregate over the
    /// series filters to it; see <c>SeriesPeriods.NativePeriodType</c> for why.
    /// </summary>
    public required PeriodType NativePeriodType { get; init; }

    public string? SourceUrl { get; init; }
    public required bool IsActive { get; init; }
    public DateTime? FirstSeenAtUtc { get; init; }
    public DateTime? LastSeenAtUtc { get; init; }

    /// <summary>Most recent current observation, or null when nothing has been collected yet.</summary>
    public SeriesLatestPointDto? Latest { get; init; }

    /// <summary>
    /// Optimistic-concurrency token, base64 of the row's <c>rowversion</c>. Send it back on an
    /// update to be told about a concurrent edit instead of silently overwriting one.
    /// </summary>
    public string? RowVersion { get; init; }
}

/// <summary>The newest value for a series, for a picker or a KPI tile.</summary>
public sealed record SeriesLatestPointDto
{
    public required DateOnly ReferenceDate { get; init; }
    public required decimal Value { get; init; }

    /// <summary>When the platform learned this value (FR-6), not when it was published.</summary>
    public required DateTime CollectedAtUtc { get; init; }
}

/// <summary>
/// The editable presentation fields of a series. Everything that describes the data itself —
/// code, unit, frequency, seasonal adjustment — comes from the publisher and stays read-only:
/// editing it here would make the platform disagree with its own source.
/// </summary>
public sealed record SeriesUpdateRequest
{
    [Required]
    [MaxLength(400)]
    public required string Title { get; init; }

    public int? CategoryId { get; init; }

    [Range(0, 10)]
    public byte? DecimalPlaces { get; init; }

    /// <summary>
    /// Hides the series from dashboards without deleting anything. Collected history is retained
    /// either way (FR-4).
    /// </summary>
    public bool IsActive { get; init; } = true;

    /// <summary>
    /// The <c>rowVersion</c> from the series you edited. Optional; supplying it turns a
    /// concurrent edit into a 409 instead of a silent overwrite.
    /// </summary>
    public string? RowVersion { get; init; }
}
