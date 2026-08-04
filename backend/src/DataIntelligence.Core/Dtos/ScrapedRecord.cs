using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DataIntelligence.Core.Dtos;

/// <summary>
/// One record as extracted from the source, before validation and persistence. This is the
/// boundary between the parser (source-specific) and everything downstream (source-agnostic):
/// swapping the source replaces the parser and nothing else.
/// </summary>
public sealed record ScrapedRecord
{
    /// <summary>Field delimiter for the row hash. U+001F UNIT SEPARATOR.</summary>
    private const char FieldSeparator = '\u001F';

    /// <summary>
    /// Stands in for an absent value. U+001E RECORD SEPARATOR - a character no scraped text can
    /// contain, so "field missing" cannot collide with any value a source might publish. Without
    /// it, null and the empty string hash alike, and a source that began publishing an empty
    /// field where it previously omitted one would register as unchanged.
    /// </summary>
    private const string NullMarker = "\u001E";

    /// <summary>The source's own stable identifier. Required — it is the dedup key (FR-3).</summary>
    public required string SourceKey { get; init; }

    public required string Title { get; init; }
    public string? CategoryCode { get; init; }
    public string? CategoryName { get; init; }
    public string? SourceUrl { get; init; }

    public decimal? PrimaryValue { get; init; }
    public decimal? SecondaryValue { get; init; }
    public int? Quantity { get; init; }
    public string? StatusText { get; init; }
    public string? CurrencyCode { get; init; }
    public DateTime? PublishedAtUtc { get; init; }

    /// <summary>Source fields with no dedicated column, keyed by attribute code.</summary>
    public IReadOnlyDictionary<string, string> ExtraAttributes { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// SHA-256 over the measure tuple, used to decide whether this observation differs from
    /// the item's last one (FR-3).
    /// </summary>
    /// <remarks>
    /// Two properties matter for correctness. Values are formatted with
    /// <see cref="CultureInfo.InvariantCulture"/>, so a server's regional settings cannot change
    /// the hash of identical data. Extra attributes are ordered with
    /// <see cref="StringComparer.Ordinal"/>, so a parser that emits fields in a different order
    /// still produces the same hash — otherwise every cycle would look like a change.
    /// <para>
    /// Fields are delimited by <see cref="FieldSeparator"/>, a character that cannot appear in
    /// scraped text, so no combination of values can forge a different tuple that hashes the same.
    /// </para>
    /// </remarks>
    public byte[] ComputeRowHash()
    {
        var builder = new StringBuilder();

        Append(builder, PrimaryValue?.ToString(CultureInfo.InvariantCulture));
        Append(builder, SecondaryValue?.ToString(CultureInfo.InvariantCulture));
        Append(builder, Quantity?.ToString(CultureInfo.InvariantCulture));
        Append(builder, StatusText);
        Append(builder, CurrencyCode);
        Append(builder, PublishedAtUtc?.ToString("O", CultureInfo.InvariantCulture));

        foreach (var pair in ExtraAttributes.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Append(builder, pair.Key);
            Append(builder, pair.Value);
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));

        static void Append(StringBuilder target, string? value) =>
            target.Append(value ?? NullMarker).Append(FieldSeparator);
    }
}
