namespace DataIntelligence.Core.Entities;

/// <summary>
/// One extension value on one snapshot. Exactly one of the value slots is populated —
/// enforced by <c>CK_ItemSnapshotAttribute_OneValue</c> in the schema.
/// </summary>
public class SnapshotAttribute
{
    public long ItemSnapshotId { get; set; }

    /// <summary>
    /// Duplicated from the parent snapshot. Carried so this table can be partition-aligned
    /// with <see cref="ItemSnapshot"/> later without a redesign (NFR Scalability).
    /// </summary>
    public DateTime CollectedAtUtc { get; set; }

    public short AttributeId { get; set; }

    public string? ValueText { get; set; }
    public decimal? ValueNumber { get; set; }
    public DateTime? ValueDate { get; set; }
    public bool? ValueBool { get; set; }

    public ItemSnapshot? Snapshot { get; set; }
    public AttributeDefinition? Attribute { get; set; }
}
