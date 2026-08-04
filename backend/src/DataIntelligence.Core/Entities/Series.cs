using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Entities;

/// <summary>
/// One measured quantity tracked through time. The dedup anchor for FR-3:
/// (DataSourceId, SeriesCode) is unique, so a re-run matches rather than duplicates.
/// </summary>
public class Series
{
    public int SeriesId { get; set; }
    public byte DataSourceId { get; set; }

    /// <summary>
    /// The publisher's own identifier where it has one (BLS: <c>CUUR0000SA0</c>). Where one API
    /// record carries several measures, as with SOFR, the code is assigned by this platform
    /// (<c>SOFR_VOL</c>) and <see cref="SourceFieldPath"/> names the field it comes from.
    /// </summary>
    public string SeriesCode { get; set; } = string.Empty;

    /// <summary>
    /// False when this platform invented the code. Recorded so nobody later mistakes one of our
    /// identifiers for the publisher's and tries to look it up upstream.
    /// </summary>
    public bool IsSourceAssignedCode { get; set; } = true;

    public string? SourceFieldPath { get; set; }

    public string Title { get; set; } = string.Empty;
    public int? CategoryId { get; set; }

    /// <summary>
    /// The unit values are stored in, verbatim from the publisher. Values are never rescaled on
    /// the way in — SOFR volume stays in billions because that is what the API publishes.
    /// Rescaling silently is how a chart ends up wrong by a factor of a thousand with nothing
    /// in the data to show it.
    /// </summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>Decimal places as published, for display fidelity.</summary>
    public byte? DecimalPlaces { get; set; }

    public SeriesFrequency Frequency { get; set; }
    public SeasonalAdjustment SeasonalAdjustment { get; set; } = SeasonalAdjustment.NotApplicable;

    public string? SourceUrl { get; set; }
    public long? FirstSeenRunId { get; set; }
    public DateTime? FirstSeenAtUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
    public byte[]? RowVersion { get; set; }

    public DataSource? DataSource { get; set; }
    public SeriesCategory? Category { get; set; }
    public ICollection<Observation> Observations { get; set; } = [];
}
