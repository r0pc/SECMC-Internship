using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Entities;

/// <summary>
/// A designated publisher (SOW 0.1). Two rows: the BLS Consumer Price Index and the New York
/// Fed's SOFR. Reference data seeded with the schema, not user-managed configuration — the
/// platform is commissioned against these publishers specifically.
/// </summary>
public class DataSource
{
    /// <summary>Seeded identifiers, stable across environments so config can refer to them.</summary>
    public const byte BlsCpiId = 1;
    public const byte NyFedSofrId = 2;

    public const string BlsCpiCode = "BLS_CPI";
    public const string NyFedSofrCode = "NYFED_SOFR";

    public byte DataSourceId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string LandingPageUrl { get; set; } = string.Empty;
    public string ApiEndpoint { get; set; } = string.Empty;

    public SourceAccessMethod AccessMethod { get; set; } = SourceAccessMethod.RestApi;

    /// <summary>BLS requires POST with a JSON body; the NY Fed serves GET.</summary>
    public string HttpMethod { get; set; } = "GET";

    public bool RequiresApiKey { get; set; }

    /// <summary>
    /// How often the publisher releases, which is not how often we poll. CPI is monthly and
    /// SOFR is business-daily, so most cycles legitimately find nothing new.
    /// </summary>
    public string PublicationCadence { get; set; } = string.Empty;

    public short CollectionIntervalMinutes { get; set; } = 60;
    public short RequestTimeoutSec { get; set; } = 30;
    public byte MaxRetries { get; set; } = 3;
    public string? UserAgent { get; set; }

    /// <summary>Compliance evidence (SOW 3), so the claim is auditable rather than assumed.</summary>
    public string? TermsOfUseUrl { get; set; }

    public DateTime? RobotsTxtCheckedAtUtc { get; set; }

    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}
