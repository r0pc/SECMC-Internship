using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Entities;

/// <summary>
/// Metadata for a source field that has not been promoted to its own snapshot column.
/// Rows are created on first sight of an attribute code, so the collector absorbs new
/// source fields without a migration.
/// </summary>
/// <remarks>
/// Deliberately a holding area. Anything a dashboard filters or aggregates on should be
/// promoted to a real <see cref="ItemSnapshot"/> column — key/value storage does not index
/// or type-check well enough to sit behind a KPI.
/// </remarks>
public class AttributeDefinition
{
    public short AttributeId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public AttributeDataType DataType { get; set; } = AttributeDataType.Text;
    public string? Unit { get; set; }
    public bool IsActive { get; set; } = true;
}
