using System.Globalization;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Collection;

/// <summary>
/// Translates a BLS (year, period) pair into a reference date and a period length.
/// </summary>
/// <remarks>
/// BLS encodes the period as a letter plus an ordinal, and a single monthly series response
/// mixes several kinds. Getting this wrong is not a parse error — it is a silent analytical
/// one: M13 is the annual average, so treating it as a thirteenth month would add a phantom
/// data point every year and drag any trend line with it.
/// <list type="bullet">
///   <item><c>M01</c>-<c>M12</c> — calendar months.</item>
///   <item><c>M13</c> — annual average.</item>
///   <item><c>S01</c>/<c>S02</c> — first and second half; <c>S03</c> — annual average.</item>
///   <item><c>Q01</c>-<c>Q04</c> — quarters; <c>Q05</c> — annual average.</item>
///   <item><c>A01</c> — annual.</item>
/// </list>
/// Lives in Core with no infrastructure dependencies so the mapping is unit-testable on its own.
/// </remarks>
public static class BlsPeriod
{
    /// <summary>
    /// Parses a BLS period token. Returns false for anything unrecognised rather than guessing,
    /// so an unfamiliar token becomes a logged rejection instead of a wrong date.
    /// </summary>
    public static bool TryParse(string? year, string? period, out DateOnly referenceDate, out PeriodType periodType)
    {
        referenceDate = default;
        periodType = PeriodType.Month;

        if (!int.TryParse(year, NumberStyles.None, CultureInfo.InvariantCulture, out var y)
            || y is < 1900 or > 2999)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(period) || period.Length != 3)
        {
            return false;
        }

        var kind = char.ToUpperInvariant(period[0]);
        if (!int.TryParse(period.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            return false;
        }

        switch (kind)
        {
            case 'M' when n is >= 1 and <= 12:
                referenceDate = new DateOnly(y, n, 1);
                periodType = PeriodType.Month;
                return true;

            case 'M' when n == 13:
                referenceDate = new DateOnly(y, 1, 1);
                periodType = PeriodType.Annual;
                return true;

            case 'S' when n is 1 or 2:
                referenceDate = new DateOnly(y, n == 1 ? 1 : 7, 1);
                periodType = PeriodType.Semiannual;
                return true;

            case 'S' when n == 3:
                referenceDate = new DateOnly(y, 1, 1);
                periodType = PeriodType.Annual;
                return true;

            case 'Q' when n is >= 1 and <= 4:
                referenceDate = new DateOnly(y, (n - 1) * 3 + 1, 1);
                periodType = PeriodType.Quarter;
                return true;

            case 'Q' when n == 5:
                referenceDate = new DateOnly(y, 1, 1);
                periodType = PeriodType.Annual;
                return true;

            case 'A':
                referenceDate = new DateOnly(y, 1, 1);
                periodType = PeriodType.Annual;
                return true;

            default:
                return false;
        }
    }
}
