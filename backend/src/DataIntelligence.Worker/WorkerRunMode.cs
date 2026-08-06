namespace DataIntelligence.Worker;

/// <summary>How the Worker was asked to run.</summary>
public enum WorkerMode
{
    /// <summary>Wait for the schedule and keep collecting, until stopped (FR-1, FR-8).</summary>
    Scheduled,

    /// <summary>Collect from every enabled source immediately, then exit.</summary>
    Once,

    /// <summary>Load history for the requested dataset(s), then exit.</summary>
    Backfill
}

/// <param name="Mode">Which of the three the command line asked for.</param>
/// <param name="IncludeCpi">Backfill CPI. Set by <c>--backfill</c> or <c>--backfill-cpi</c>.</param>
/// <param name="IncludeSofr">Backfill SOFR. Set by <c>--backfill</c> or <c>--backfill-sofr</c>.</param>
/// <param name="CpiFromYear">
/// First year of CPI to load, from <c>--from</c>. Applies to CPI only: SOFR has one start date and
/// no chunking, so there is nothing to choose.
/// </param>
public sealed record WorkerRunMode(
    WorkerMode Mode,
    bool IncludeCpi = false,
    bool IncludeSofr = false,
    int CpiFromYear = WorkerRunMode.EarliestCpiYear)
{
    /// <summary>The first year BLS published this index. Nothing exists before it to load.</summary>
    public const int EarliestCpiYear = 1913;

    /// <summary>
    /// The first SOFR effective date. Verified against the API rather than assumed: asking for
    /// 2018-01-01 onwards returns nothing before this day.
    /// </summary>
    public static readonly DateOnly FirstSofrDate = new(2018, 4, 2);

    public bool IsOneShot => Mode is WorkerMode.Once or WorkerMode.Backfill;
}
