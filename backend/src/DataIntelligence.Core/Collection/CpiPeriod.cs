using System.Globalization;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Collection;

/// <summary>
/// Translates a BLS (year, period) pair into the period code, reference date and period length
/// that <c>core.CpiObservation</c> stores.
/// </summary>
/// <remarks>
/// BLS encodes the period as a letter plus an ordinal, and one monthly response mixes several
/// kinds. Getting this wrong is not a parse error — it is a silent analytical one: M13 is the
/// annual average, so treating it as a thirteenth month adds a phantom data point every year and
/// drags any trend line with it.
/// <list type="bullet">
///   <item><c>M01</c>-<c>M12</c> — calendar months.</item>
///   <item><c>M13</c> — the annual average, the CSV's "Annual" column.</item>
///   <item><c>S01</c>/<c>S02</c> — first and second half, the CSV's HALF1 and HALF2.</item>
/// </list>
/// Nothing else is accepted. CUUR0000SA0 is a monthly series and never emits the quarterly or
/// annual tokens BLS defines for other series, so a token outside this set means either the
/// wrong series was requested or the contract changed — both worth a logged rejection rather
/// than a guess. <c>CK_Cpi_PeriodCode</c> enforces the same set in the database.
/// <para>
/// Lives in Core with no infrastructure dependencies so the mapping is unit-testable on its own.
/// </para>
/// </remarks>
public static class CpiPeriod
{
    /// <summary>The annual-average token. Dated 1 January, like <c>S01</c> — which is exactly why
    /// the period code is part of the natural key and the reference date alone is not.</summary>
    public const string AnnualCode = "M13";

    public const string FirstHalfCode = "S01";
    public const string SecondHalfCode = "S02";

    /// <summary>
    /// Parses a BLS period token. Returns false for anything outside the stored set rather than
    /// guessing, so an unfamiliar token becomes a logged rejection instead of a wrong date.
    /// </summary>
    public static bool TryParse(
        string? year,
        string? period,
        out short referenceYear,
        out string periodCode,
        out DateOnly referenceDate,
        out PeriodType periodType)
    {
        referenceYear = 0;
        periodCode = string.Empty;
        referenceDate = default;
        periodType = PeriodType.Month;

        if (!int.TryParse(year, NumberStyles.None, CultureInfo.InvariantCulture, out var y)
            || y is < 1900 or > 2999)
        {
            return false;
        }

        if (!IsKnownPeriodCode(period))
        {
            return false;
        }

        referenceYear = (short)y;
        periodCode = period!.ToUpperInvariant();
        periodType = PeriodTypeFor(periodCode);
        referenceDate = ReferenceDateFor(referenceYear, periodCode);
        return true;
    }

    /// <summary>Whether the token is one of the fifteen this platform stores.</summary>
    public static bool IsKnownPeriodCode(string? periodCode)
    {
        if (periodCode is not { Length: 3 })
        {
            return false;
        }

        var normalized = periodCode.ToUpperInvariant();

        if (normalized is FirstHalfCode or SecondHalfCode or AnnualCode)
        {
            return true;
        }

        return normalized[0] == 'M'
            && int.TryParse(normalized.AsSpan(1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var month)
            && month is >= 1 and <= 12;
    }

    /// <summary>The period length a token denotes.</summary>
    /// <exception cref="ArgumentException">The token is not one this platform stores.</exception>
    public static PeriodType PeriodTypeFor(string periodCode)
    {
        if (!IsKnownPeriodCode(periodCode))
        {
            throw new ArgumentException($"'{periodCode}' is not a stored CPI period code.", nameof(periodCode));
        }

        return periodCode.ToUpperInvariant() switch
        {
            AnnualCode => PeriodType.Annual,
            FirstHalfCode or SecondHalfCode => PeriodType.Semiannual,
            _ => PeriodType.Month
        };
    }

    /// <summary>
    /// First day of the period. M13 and S01 both start on 1 January, and S02 on 1 July — which is
    /// why (year, period code) is the natural key rather than the date.
    /// </summary>
    /// <exception cref="ArgumentException">The token is not one this platform stores.</exception>
    public static DateOnly ReferenceDateFor(short referenceYear, string periodCode)
    {
        if (!IsKnownPeriodCode(periodCode))
        {
            throw new ArgumentException($"'{periodCode}' is not a stored CPI period code.", nameof(periodCode));
        }

        var normalized = periodCode.ToUpperInvariant();

        var month = normalized switch
        {
            AnnualCode or FirstHalfCode => 1,
            SecondHalfCode => 7,
            _ => int.Parse(normalized.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture)
        };

        return new DateOnly(referenceYear, month, 1);
    }
}
