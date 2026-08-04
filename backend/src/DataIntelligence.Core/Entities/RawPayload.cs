namespace DataIntelligence.Core.Entities;

/// <summary>
/// The untouched response body for a run, stored compressed. Diagnostic only: it lets a
/// layout change be diagnosed after the fact and a cycle be re-parsed without re-fetching
/// (SOW 9, "target site changes layout"). Purged on a shorter window than curated data.
/// </summary>
public class RawPayload
{
    public long RawPayloadId { get; set; }
    public long CollectionRunId { get; set; }
    public DateTime FetchedAtUtc { get; set; }
    public string? ContentType { get; set; }

    /// <summary>
    /// SHA-256 of the uncompressed body. An unchanged hash across consecutive runs means the
    /// source republished nothing, which is the cheapest possible short-circuit.
    /// </summary>
    public byte[] ContentHash { get; set; } = [];

    public int SizeBytes { get; set; }

    /// <summary>GZip-compressed body. Written via SQL Server's COMPRESS(); read via DECOMPRESS().</summary>
    public byte[] CompressedContent { get; set; } = [];

    public CollectionRun? Run { get; set; }
}
