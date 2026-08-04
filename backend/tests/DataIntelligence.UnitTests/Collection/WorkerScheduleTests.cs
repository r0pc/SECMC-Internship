using DataIntelligence.Infrastructure.Collection;

namespace DataIntelligence.UnitTests.Collection;

/// <summary>
/// Schedule arithmetic for FR-1's hourly cycle. Boundary behaviour is easy to get quietly
/// wrong and expensive to notice in production, so it is tested directly.
/// </summary>
public class WorkerScheduleTests
{
    private static readonly TimeSpan Hourly = TimeSpan.FromHours(1);

    private static DateTime NextRun(DateTime now, TimeSpan? interval = null, bool align = true) =>
        CollectionSchedule.GetNextRunTime(now, interval ?? Hourly, align);

    [Fact]
    public void AlignedSchedule_SnapsToTheTopOfTheHour()
    {
        var next = NextRun(new DateTime(2026, 8, 4, 10, 17, 42, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 8, 4, 11, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void AlignedSchedule_AdvancesAFullInterval_WhenAlreadyOnABoundary()
    {
        // Must not return "now", which would spin the loop with a zero delay.
        var next = NextRun(new DateTime(2026, 8, 4, 10, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 8, 4, 11, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void AlignedSchedule_RollsOverMidnight()
    {
        var next = NextRun(new DateTime(2026, 8, 4, 23, 30, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void AlignedSchedule_HandlesSubHourIntervals()
    {
        var next = NextRun(new DateTime(2026, 8, 4, 10, 7, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(15));

        Assert.Equal(new DateTime(2026, 8, 4, 10, 15, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void AlignedSchedule_DoesNotEmitAShortFinalCycle_ForIntervalsThatDoNotDivideTheDay()
    {
        // 7 hours: boundaries at 00:00, 07:00, 14:00, 21:00. The next one would be 28:00, so the
        // schedule rolls to the following midnight rather than firing a 3-hour stub.
        var next = NextRun(new DateTime(2026, 8, 4, 22, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(7));

        Assert.Equal(new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void UnalignedSchedule_AddsTheIntervalToNow()
    {
        var now = new DateTime(2026, 8, 4, 10, 17, 42, DateTimeKind.Utc);

        Assert.Equal(now.AddHours(1), NextRun(now, align: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(59)]
    public void AlignedSchedule_NeverReturnsATimeInThePast(int minute)
    {
        var now = new DateTime(2026, 8, 4, 10, minute, 0, DateTimeKind.Utc);

        Assert.True(NextRun(now) > now);
    }
}
