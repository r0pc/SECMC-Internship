namespace DataIntelligence.Core.Entities;

/// <summary>
/// The untouched API response for a run, stored compressed. Diagnostic: it lets a parse failure
/// be reproduced and a cycle re-parsed without re-requesting, which matters when BLS caps the
/// daily query budget.
/// </summary>
public class RawPayload
{
    public long RawPayloadId { get; set; }
    public long CollectionRunId { get; set; }
    public DateTime FetchedAtUtc { get; set; }
    public string? ContentType { get; set; }

    /// <summary>
    /// SHA-256 of the uncompressed body. An unchanged hash between consecutive runs means the
    /// publisher released nothing new — the cheapest short-circuit available, and the common
    /// case when polling monthly data hourly.
    /// </summary>
    public byte[] ContentHash { get; set; } = [];

    public int SizeBytes { get; set; }
    public byte[] CompressedContent { get; set; } = [];

    public CollectionRun? Run { get; set; }
}
