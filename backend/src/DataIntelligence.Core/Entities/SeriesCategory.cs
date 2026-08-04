namespace DataIntelligence.Core.Entities;

/// <summary>
/// Grouping for dashboard drill-down (FR-11). Self-referencing because CPI's item structure is
/// a hierarchy: All items -> Food and beverages -> Food -> Food at home.
/// </summary>
public class SeriesCategory
{
    public int CategoryId { get; set; }
    public int? ParentCategoryId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public short SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public SeriesCategory? Parent { get; set; }
    public ICollection<SeriesCategory> Children { get; set; } = [];
    public ICollection<Series> Series { get; set; } = [];
}
