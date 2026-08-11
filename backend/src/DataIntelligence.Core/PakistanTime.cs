// backend/src/DataIntelligence.Core/PakistanTime.cs
namespace DataIntelligence.Core;

/// <summary>
/// The clock the platform records and reports in: Pakistan Standard Time, UTC+05:00.
/// </summary>
/// <remarks>
/// Every <c>...AtPkt</c> column holds a PKT wall-clock reading, so there is exactly one place that
/// converts and one definition of "now". Anything calling <c>DateTime.UtcNow</c> or
/// <c>TimeProvider.GetUtcNow().UtcDateTime</c> directly would write a timestamp five hours adrift
/// of every other row, and nothing in the schema would catch it — the columns are
/// <c>datetime2</c>, which carries no offset to disagree with.
/// <para>
/// A fixed offset rather than a named zone lookup. Pakistan has observed no daylight saving since
/// 2009, so UTC+5 is the whole rule; <c>TimeZoneInfo.FindSystemTimeZoneById</c> would add a
/// platform-dependent lookup ("Pakistan Standard Time" on Windows, "Asia/Karachi" elsewhere) that
/// can throw at runtime on a host whose tz database is incomplete. If Pakistan reintroduces DST,
/// this is the one place that has to change.
/// </para>
/// </remarks>
public static class PakistanTime
{
    /// <summary>PKT's offset from UTC.</summary>
    public static readonly TimeSpan Offset = TimeSpan.FromHours(5);

    /// <summary>The IANA and Windows identifiers, for anything that needs to name the zone.</summary>
    public const string IanaId = "Asia/Karachi";

    /// <summary>
    /// The current PKT wall-clock reading, with <see cref="DateTimeKind.Unspecified"/>.
    /// </summary>
    /// <remarks>
    /// Unspecified is deliberate and is what makes this safe to store. Marking it
    /// <see cref="DateTimeKind.Local"/> would invite anything downstream to "helpfully" convert it
    /// again using the host's zone — which on a UTC server would shift it back five hours — and
    /// marking it <see cref="DateTimeKind.Utc"/> would be a lie. A wall-clock reading with no kind
    /// is exactly what a <c>datetime2</c> column holds.
    /// </remarks>
    public static DateTime Now(TimeProvider timeProvider) =>
        DateTime.SpecifyKind(
            timeProvider.GetUtcNow().UtcDateTime.Add(Offset), DateTimeKind.Unspecified);

    /// <summary>Today's date in PKT.</summary>
    public static DateOnly Today(TimeProvider timeProvider) => DateOnly.FromDateTime(Now(timeProvider));

    /// <summary>Reads a stored PKT timestamp as the instant it names.</summary>
    public static DateTimeOffset ToOffset(DateTime pkt) =>
        new(DateTime.SpecifyKind(pkt, DateTimeKind.Unspecified), Offset);
}
