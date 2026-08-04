namespace DataIntelligence.Core.Entities;

/// <summary>
/// A distinct entity tracked at the source, identified by the source's own stable key.
/// This is the deduplication anchor for FR-3: <see cref="SourceKey"/> is unique, so a
/// re-run matches the existing row instead of creating a second one.
/// </summary>
/// <remarks>
/// Only slowly-changing descriptive fields live here. Anything that moves over time is a
/// <see cref="ItemSnapshot"/>. Rows are never deleted — an item that stops appearing at the
/// source is marked <see cref="IsActive"/> = false so its history survives (FR-4).
/// </remarks>
public class Item
{
    public int ItemId { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int? CategoryId { get; set; }
    public string? SourceUrl { get; set; }

    public long FirstSeenRunId { get; set; }
    public DateTime FirstSeenAtUtc { get; set; }

    /// <summary>
    /// Bumped every cycle the item is observed, whether or not its values changed. This is
    /// what makes skipping unchanged snapshots lossless — "still present at T" is recorded
    /// here rather than as a duplicate fact row.
    /// </summary>
    public DateTime LastSeenAtUtc { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>SQL Server rowversion, for optimistic concurrency.</summary>
    public byte[]? RowVersion { get; set; }

    public Category? Category { get; set; }
    public ICollection<ItemSnapshot> Snapshots { get; set; } = [];
}
