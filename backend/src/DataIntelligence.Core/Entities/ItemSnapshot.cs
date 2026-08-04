namespace DataIntelligence.Core.Entities;

/// <summary>
/// The fact table: one immutable observation of an item at a point in time (FR-4, FR-6).
/// Append-only — nothing here is ever updated or deleted outside the archival policy.
/// </summary>
/// <remarks>
/// The measure properties below are placeholders with real types, pending the
/// <c>[DATA SOURCE — TBD]</c> sign-off (SOW 0.1). They get business names at that point;
/// the rename is a migration plus a parser-config edit, and touches nothing else.
/// </remarks>
public class ItemSnapshot
{
    public long ItemSnapshotId { get; set; }
    public int ItemId { get; set; }
    public long CollectionRunId { get; set; }

    /// <summary>The collection timestamp required by FR-6, and the future partition key.</summary>
    public DateTime CollectedAtUtc { get; set; }

    /// <summary>Computed and persisted by SQL Server as yyyyMMdd; never set in code.</summary>
    public int CollectedDateKey { get; private set; }

    // ---- Measures (renamed at source sign-off) ----
    public decimal? PrimaryValue { get; set; }
    public decimal? SecondaryValue { get; set; }
    public int? Quantity { get; set; }
    public string? StatusText { get; set; }
    public string? CurrencyCode { get; set; }

    /// <summary>The source's own timestamp for the observation, where it publishes one.</summary>
    public DateTime? PublishedAtUtc { get; set; }

    /// <summary>
    /// SHA-256 over the normalised measure tuple. Equal to the item's previous hash means
    /// nothing moved this cycle — the basis of deduplication (FR-3).
    /// </summary>
    public byte[] RowHash { get; set; } = [];

    /// <summary>
    /// False only when <c>StoreUnchangedSnapshots</c> is on and this row repeats the previous
    /// one. In the default configuration unchanged rows are not written at all, so this is true.
    /// </summary>
    public bool HasChanged { get; set; } = true;

    public Item? Item { get; set; }
    public CollectionRun? Run { get; set; }
    public ICollection<SnapshotAttribute> Attributes { get; set; } = [];
}
