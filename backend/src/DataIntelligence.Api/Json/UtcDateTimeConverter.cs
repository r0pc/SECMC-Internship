using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataIntelligence.Api.Json;

/// <summary>
/// Serialises every <see cref="DateTime"/> with an explicit <c>Z</c>.
/// </summary>
/// <remarks>
/// SQL Server's <c>datetime2</c> carries no offset, so EF materialises timestamps with
/// <see cref="DateTimeKind.Unspecified"/> and the default serialiser writes them without a
/// suffix. JavaScript then parses <c>"2026-08-04T11:04:41"</c> as <em>local</em> time, so a
/// collection timestamp would shift by the viewer's offset — a bug that is invisible in UTC+0
/// and silently wrong everywhere else.
/// <para>
/// Every timestamp the platform stores is UTC by construction, so stamping the kind on the way
/// out is a statement of fact rather than a conversion. Applies to <c>DateTime?</c> too:
/// System.Text.Json unwraps the nullable before choosing a converter.
/// </para>
/// </remarks>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetDateTime();

        return value.Kind switch
        {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => value
        };
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => value
        };

        // Round-trip format ("O") on a UTC value ends in Z, which is what makes it unambiguous.
        writer.WriteStringValue(utc.ToString("O"));
    }
}
