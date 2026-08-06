using System.Globalization;

namespace DataIntelligence.Worker;

/// <summary>
/// Reads the Worker's run mode from its command line.
/// </summary>
/// <remarks>
/// Parsed from the raw arguments rather than through configuration: the command-line provider
/// expects key/value pairs, and bare switches would each need a switch mapping to bind — more
/// machinery than four flags are worth.
/// <para>
/// A separate class rather than a local function in <c>Program</c> so the rules can be tested.
/// Most of them are refusals, and a refusal that silently stops refusing is not something a
/// manual check would notice.
/// </para>
/// </remarks>
public static class WorkerCommandLine
{
    public const string Usage =
        "Usage: dotnet run --project src\\DataIntelligence.Worker "
        + "[--once | [--backfill | --backfill-cpi | --backfill-sofr] [--from <year>]]\n"
        + "       --from sets the first CPI year and applies to --backfill and --backfill-cpi.";

    /// <summary>
    /// Reads the run mode, or explains why the arguments do not describe one.
    /// </summary>
    /// <param name="utcNow">
    /// Current time, for validating <c>--from</c> against. Injected so the boundary is testable
    /// without waiting for a year to pass.
    /// </param>
    public static bool TryParse(
        string[] args,
        DateTime utcNow,
        out WorkerRunMode runMode,
        out string? error)
    {
        runMode = new WorkerRunMode(WorkerMode.Scheduled);
        error = null;

        var once = HasFlag(args, "--once");

        // Additive, so --backfill-cpi --backfill-sofr means the same as --backfill.
        var includeCpi = HasFlag(args, "--backfill") || HasFlag(args, "--backfill-cpi");
        var includeSofr = HasFlag(args, "--backfill") || HasFlag(args, "--backfill-sofr");
        var backfill = includeCpi || includeSofr;

        if (once && backfill)
        {
            error = "--once and the backfill flags do different things; pass one or the other.";
            return false;
        }

        if (once)
        {
            runMode = new WorkerRunMode(WorkerMode.Once);
            return true;
        }

        var hasFrom = TryReadOption(args, "--from", out var rawYear);

        if (!backfill)
        {
            // --from on its own is a mistake worth naming: on the scheduled path it would be
            // silently ignored, and the run would look like it had honoured it.
            if (hasFrom)
            {
                error = "--from applies to --backfill or --backfill-cpi.";
                return false;
            }

            return true;
        }

        // Likewise when only SOFR was asked for. SOFR has one start date and needs no chunking,
        // so --from would have nothing to act on.
        if (hasFrom && !includeCpi)
        {
            error = "--from sets the first CPI year, and --backfill-sofr does not collect CPI. "
                + "Use --backfill or --backfill-cpi.";
            return false;
        }

        var fromYear = WorkerRunMode.EarliestCpiYear;

        if (hasFrom)
        {
            if (!int.TryParse(rawYear, NumberStyles.None, CultureInfo.InvariantCulture, out fromYear))
            {
                error = $"--from expects a four-digit year; got '{rawYear ?? "nothing"}'.";
                return false;
            }

            if (fromYear < WorkerRunMode.EarliestCpiYear || fromYear > utcNow.Year)
            {
                error = $"--from must be between {WorkerRunMode.EarliestCpiYear} (the first "
                    + $"published figure) and {utcNow.Year}; got {fromYear}.";
                return false;
            }
        }

        runMode = new WorkerRunMode(WorkerMode.Backfill, includeCpi, includeSofr, fromYear);
        return true;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Reads <c>--name value</c> and <c>--name=value</c> alike, because both are habitual.</summary>
    private static bool TryReadOption(string[] args, string name, out string? value)
    {
        value = null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = args[i][(name.Length + 1)..];
                return true;
            }

            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                value = i + 1 < args.Length ? args[i + 1] : null;
                return true;
            }
        }

        return false;
    }
}
