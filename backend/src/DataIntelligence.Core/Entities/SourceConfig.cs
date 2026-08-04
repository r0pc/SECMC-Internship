namespace DataIntelligence.Core.Entities;

/// <summary>
/// The single designated data source (SOW 0.1). Constrained to exactly one row
/// (<c>CK_SourceConfig_Single</c>): the platform collects from one source by design.
/// </summary>
/// <remarks>
/// Behaviour is driven by configuration (<c>Collection:*</c>); this row is the database's
/// own record of what the platform is pointed at, upserted by the Worker at startup so
/// data lineage is answerable in SQL without reading a config file.
/// </remarks>
public class SourceConfig
{
    /// <summary>Always 1 — enforced by a check constraint.</summary>
    public const byte SingletonId = 1;

    public byte SourceConfigId { get; set; } = SingletonId;
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string CollectionUrl { get; set; } = string.Empty;
    public short CollectionIntervalMinutes { get; set; } = 60;
    public short RequestTimeoutSec { get; set; } = 30;
    public byte MaxRetries { get; set; } = 3;
    public string? UserAgent { get; set; }

    /// <summary>Evidence that robots.txt was evaluated before collecting (SOW 3 — Compliance).</summary>
    public DateTime? RobotsTxtCheckedAtUtc { get; set; }

    public bool IsEnabled { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}
