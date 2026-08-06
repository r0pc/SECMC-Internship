namespace DataIntelligence.Core.Dtos;

/// <summary>
/// The period a collection cycle asks the publisher for, when it is not the adapter's default.
/// </summary>
/// <remarks>
/// Exists for backfilling. The scheduled cycle deliberately asks for a narrow, recent window —
/// two years of CPI, the current year of SOFR — because that is what the dashboards read and
/// because re-requesting a century of settled figures every hour would be absurd. Loading the
/// history is a separate, deliberate act, and this is how it says which part of it it wants.
/// </remarks>
/// <param name="From">First period to request, inclusive.</param>
/// <param name="To">Last period to request, inclusive.</param>
public sealed record CollectionWindow(DateOnly From, DateOnly To)
{
    /// <summary>A window covering whole calendar years, inclusive of both.</summary>
    public static CollectionWindow ForYears(int fromYear, int toYear)
    {
        if (fromYear > toYear)
        {
            throw new ArgumentOutOfRangeException(nameof(fromYear),
                $"The window starts in {fromYear} and ends in {toYear}.");
        }

        return new CollectionWindow(new DateOnly(fromYear, 1, 1), new DateOnly(toYear, 12, 31));
    }

    public int FromYear => From.Year;

    public int ToYear => To.Year;

    /// <summary>Calendar years the window touches, counting both ends.</summary>
    public int YearSpan => ToYear - FromYear + 1;

    public override string ToString() =>
        $"{From:yyyy-MM-dd} to {To:yyyy-MM-dd}";
}
