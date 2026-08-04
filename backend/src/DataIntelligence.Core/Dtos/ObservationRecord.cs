using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Dtos;

/// <summary>
/// One observation as extracted from a publisher's response, before validation and persistence.
/// The boundary between the source adapters (publisher-specific) and everything downstream
/// (publisher-agnostic): adding a third source adds an adapter and changes nothing else.
/// </summary>
public sealed record ObservationRecord
{
    /// <summary>Field delimiter for the row hash. U+001F UNIT SEPARATOR.</summary>
    private const char FieldSeparator = '\u001F';

    /// <summary>
    /// Stands in for an absent value. U+001E RECORD SEPARATOR — a character no published value
    /// can contain, so "no annotation" cannot collide with a real one.
    /// </summary>
    private const string NullMarker = "\u001E";

    public required string SeriesCode { get; init; }

    /// <summary>Period start: 2026-06-01 for CPI "M06", 2026-07-31 for a SOFR business day.</summary>
    public required DateOnly ReferenceDate { get; init; }

    public required PeriodType PeriodType { get; init; }

    /// <summary>The publisher's own period token, kept verbatim for traceability.</summary>
    public string? SourcePeriodCode { get; init; }

    public required decimal Value { get; init; }

    /// <summary>BLS footnote codes, or the NY Fed's revisionIndicator.</summary>
    public string? SourceAnnotation { get; init; }

    /// <summary>
    /// SHA-256 over the value tuple, used to decide whether the publisher has actually changed
    /// anything since the last collection (FR-3).
    /// </summary>
    /// <remarks>
    /// The annotation is part of the hash on purpose. BLS flips a footnote from preliminary to
    /// revised without necessarily moving the number, and that transition is itself meaningful
    /// for economic data — it deserves a new vintage rather than being silently swallowed.
    /// <para>
    /// <see cref="CultureInfo.InvariantCulture"/> throughout, so a host's regional settings
    /// cannot change the hash of identical data and make a failover look like a mass revision.
    /// </para>
    /// </remarks>
    public byte[] ComputeRowHash()
    {
        var builder = new StringBuilder();

        // "G29" normalises the scale that decimal otherwise preserves: 333.95 and 333.950 are
        // the same number, but ToString() renders them differently, which would log a phantom
        // revision every time a publisher changed its formatting.
        Append(builder, Value.ToString("G29", CultureInfo.InvariantCulture));
        Append(builder, SourceAnnotation);

        return SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));

        static void Append(StringBuilder target, string? value) =>
            target.Append(value ?? NullMarker).Append(FieldSeparator);
    }
}
