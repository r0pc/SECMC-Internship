using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataIntelligence.Core;

namespace DataIntelligence.Api.Json;

/// <summary>
/// Serialises every <see cref="DateTime"/> with an explicit <c>+05:00</c> offset.
/// </summary>
/// <remarks>
/// SQL Server's <c>datetime2</c> carries no offset, so EF materialises timestamps with
/// <see cref="DateTimeKind.Unspecified"/> and the default serialiser writes them without a suffix.
/// JavaScript then parses <c>"2026-08-10T16:04:41"</c> as <em>local</em> time, so a collection
/// timestamp shifts by whatever offset the viewer happens to be in — a bug that is invisible on a
/// machine already set to Pakistan time and silently wrong everywhere else.
/// <para>
/// Every <c>...AtPkt</c> column holds a Pakistan wall-clock reading, so stamping the offset on the
/// way out is a statement of fact rather than a conversion: the same instant, finally said out
/// loud. This class used to write <c>Z</c>, which was the same statement about a different clock —
/// leaving it that way after the columns moved to PKT would have reported every timestamp five
/// hours early to every client, with nothing in the pipeline able to notice.
/// </para>
/// Applies to <c>DateTime?</c> too: System.Text.Json unwraps the nullable before choosing a
/// converter.
/// </remarks>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    /// <summary>Round-trippable to the millisecond the columns actually store.</summary>
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffzzz";

    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // An incoming value carrying its own offset is converted into the PKT wall clock the
        // platform stores; one without an offset is taken at face value as already being PKT,
        // which is what a filter typed as "2026-08-01" by a user in Karachi means.
        if (reader.TryGetDateTimeOffset(out var offset))
        {
            return DateTime.SpecifyKind(
                offset.ToOffset(PakistanTime.Offset).DateTime, DateTimeKind.Unspecified);
        }

        return DateTime.SpecifyKind(reader.GetDateTime(), DateTimeKind.Unspecified);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(
            PakistanTime.ToOffset(value).ToString(Format, CultureInfo.InvariantCulture));
}
