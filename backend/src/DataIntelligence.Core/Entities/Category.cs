namespace DataIntelligence.Core.Entities;

/// <summary>
/// Dimension for dashboard drill-down (FR-11). Self-referencing so a two-level source
/// taxonomy needs no schema change.
/// </summary>
public class Category
{
    public int CategoryId { get; set; }
    public int? ParentCategoryId { get; set; }

    /// <summary>The code as published by the source. Unique — this is the match key on upsert.</summary>
    public string Code { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public short SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public Category? Parent { get; set; }
    public ICollection<Category> Children { get; set; } = [];
    public ICollection<Item> Items { get; set; } = [];
}
