using System.ComponentModel.DataAnnotations;

namespace DataIntelligence.Infrastructure.Collection;

/// <summary>
/// The selector profile that turns the source's markup into records. This is the whole of the
/// source-specific knowledge in the system.
/// </summary>
public sealed class ParserOptions
{
    /// <summary>
    /// XPath matching one node per record — the repeating row, card, or list item.
    /// </summary>
    /// <example><c>//div[contains(@class,'listing')]</c></example>
    public string RecordSelector { get; set; } = string.Empty;

    /// <summary>
    /// Field extractors keyed by target property: <c>SourceKey</c>, <c>Title</c>,
    /// <c>CategoryCode</c>, <c>SourceUrl</c>, <c>PrimaryValue</c>, <c>SecondaryValue</c>,
    /// <c>Quantity</c>, <c>StatusText</c>, <c>CurrencyCode</c>, <c>PublishedAtUtc</c>.
    /// Any other key is stored as an extension attribute under that name.
    /// </summary>
    public Dictionary<string, FieldSelector> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Configured but unmatched selectors tolerated per record before it is rejected as schema
    /// drift. Zero is strict; raise it when the source legitimately omits optional fields.
    /// </summary>
    [Range(0, 20)]
    public int MaxMissingOptionalFields { get; set; } = 20;

    /// <summary>True once a record selector has been supplied — i.e. the source is known.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(RecordSelector);
}

/// <summary>How to read one field out of a record node.</summary>
public sealed class FieldSelector
{
    /// <summary>
    /// XPath evaluated relative to the record node. Must start with <c>.</c> to stay scoped —
    /// a selector starting with <c>//</c> silently searches the whole document and pulls the
    /// first record's value into every row.
    /// </summary>
    public string Selector { get; set; } = string.Empty;

    /// <summary>
    /// Attribute to read instead of the node's text, e.g. <c>href</c> or <c>data-id</c>.
    /// </summary>
    public string? Attribute { get; set; }

    /// <summary>Target type. Parse failures reject the record as a type mismatch.</summary>
    public FieldType Type { get; set; } = FieldType.Text;

    /// <summary>
    /// Regex applied after extraction; the first capturing group becomes the value. Use it to
    /// pull a number out of decorated text such as <c>"Now £19.99 (was £24.50)"</c>.
    /// </summary>
    public string? ExtractPattern { get; set; }

    /// <summary>
    /// Characters stripped before parsing a number — currency symbols, thousands separators.
    /// </summary>
    public string? StripCharacters { get; set; }

    /// <summary>
    /// Exact date format for <see cref="FieldType.DateTime"/>, e.g. <c>dd/MM/yyyy HH:mm</c>.
    /// Given one, parsing is exact and culture-invariant; without one it falls back to a
    /// permissive invariant parse, which is likelier to misread ambiguous dates.
    /// </summary>
    public string? DateFormat { get; set; }

    /// <summary>
    /// Reject the record when this selector matches nothing. Set on the fields that make a
    /// record meaningful; <c>SourceKey</c> and <c>Title</c> are always required regardless.
    /// </summary>
    public bool Required { get; set; }
}

public enum FieldType
{
    Text,
    Decimal,
    Integer,
    DateTime
}
