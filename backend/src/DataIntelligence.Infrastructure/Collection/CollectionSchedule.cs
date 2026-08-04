namespace DataIntelligence.Infrastructure.Collection;

/// <summary>
/// Works out when the next collection cycle is due (FR-1).
/// </summary>
/// <remarks>
/// A plain function rather than a cron library: the requirement is an hourly cycle, and a cron
/// parser would be a dependency and a configuration surface bought for one expression. If the
/// sponsor later needs genuinely irregular schedules, this is the single place that changes.
/// </remarks>
public static class CollectionSchedule
{
    /// <summary>
    /// The next run time strictly after <paramref name="utcNow"/>.
    /// </summary>
    /// <param name="utcNow">Current UTC time.</param>
    /// <param name="interval">Gap between cycles.</param>
    /// <param name="alignToClock">
    /// Snap to wall-clock boundaries, so an hourly cycle fires at :00 regardless of when the
    /// service started. Keeps run times predictable across restarts and deployments.
    /// </param>
    public static DateTime GetNextRunTime(DateTime utcNow, TimeSpan interval, bool alignToClock)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Interval must be positive.");
        }

        if (!alignToClock)
        {
            return utcNow + interval;
        }

        // Measured from the start of the day, so intervals that divide 24 hours evenly land on
        // familiar boundaries (:00, :15, :30).
        var dayStart = utcNow.Date;
        var completed = Math.Floor((utcNow - dayStart) / interval);
        var next = dayStart + interval * (completed + 1);

        // An interval that does not divide the day evenly would otherwise produce a short stub
        // cycle at midnight; roll to the next day's first boundary instead.
        var nextMidnight = dayStart.AddDays(1);
        return next > nextMidnight ? nextMidnight : next;
    }
}
