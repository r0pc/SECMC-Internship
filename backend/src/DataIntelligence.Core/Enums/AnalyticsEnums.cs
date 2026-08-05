namespace DataIntelligence.Core.Enums;

/// <summary>Bucket width for a trend chart (FR-10, FR-11).</summary>
public enum TrendGranularity
{
    /// <summary>Chosen from the series' frequency and the requested range. The default.</summary>
    Auto,

    /// <summary>One point per observation, unbucketed.</summary>
    Point,

    Month,
    Quarter,
    Year
}

/// <summary>Sort direction for observation pages.</summary>
public enum SortDirection
{
    /// <summary>Oldest first — chart order.</summary>
    Ascending,

    /// <summary>Newest first — table order.</summary>
    Descending
}
