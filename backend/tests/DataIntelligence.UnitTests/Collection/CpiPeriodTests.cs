using DataIntelligence.Core.Collection;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.UnitTests.Collection;

/// <summary>
/// The BLS period mapping. Getting this wrong is not a parse error but a silent analytical one,
/// so it is tested on its own (SOW 11.1).
/// </summary>
public class CpiPeriodTests
{
    [Theory]
    [InlineData("M01", 1, PeriodType.Month)]
    [InlineData("M06", 6, PeriodType.Month)]
    [InlineData("M12", 12, PeriodType.Month)]
    // The annual average, dated 1 January — the same reference date as that year's M01, which is
    // exactly why the period code is part of the key and the date alone is not.
    [InlineData("M13", 1, PeriodType.Annual)]
    [InlineData("S01", 1, PeriodType.Semiannual)]
    [InlineData("S02", 7, PeriodType.Semiannual)]
    public void ParsesEveryStoredPeriod(string period, int expectedMonth, PeriodType expectedType)
    {
        var parsed = CpiPeriod.TryParse("2026", period,
            out var year, out var code, out var referenceDate, out var periodType);

        Assert.True(parsed);
        Assert.Equal((short)2026, year);
        Assert.Equal(period, code);
        Assert.Equal(new DateOnly(2026, expectedMonth, 1), referenceDate);
        Assert.Equal(expectedType, periodType);
    }

    [Theory]
    // Quarterly and annual tokens exist in the BLS vocabulary but never for a monthly series;
    // seeing one means the wrong series was requested or the contract changed.
    [InlineData("Q01")]
    [InlineData("A01")]
    [InlineData("S03")]
    // 'M1' sorts inside 'M01'..'M13', so a range check would accept it as a second spelling of
    // January. It is rejected instead.
    [InlineData("M1")]
    [InlineData("M00")]
    [InlineData("M14")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsAnythingOutsideTheStoredSet(string? period)
    {
        Assert.False(CpiPeriod.TryParse("2026", period, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData("1899")]
    [InlineData("3000")]
    [InlineData("nineteen")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsAnImplausibleYear(string? year)
    {
        Assert.False(CpiPeriod.TryParse(year, "M06", out _, out _, out _, out _));
    }

    [Fact]
    public void NormalisesTheTokenToUpperCase()
    {
        Assert.True(CpiPeriod.TryParse("2026", "m06", out _, out var code, out _, out _));
        Assert.Equal("M06", code);
    }

    [Fact]
    public void AnnualAndFirstHalfShareAReferenceDate()
    {
        // The collision the (year, period code) key exists to survive. If the date alone were the
        // key, one of these would silently overwrite the other.
        Assert.Equal(
            CpiPeriod.ReferenceDateFor(2026, "M13"),
            CpiPeriod.ReferenceDateFor(2026, "S01"));
    }

    [Fact]
    public void RejectsAnUnknownCodeRatherThanGuessing()
    {
        Assert.Throws<ArgumentException>(() => CpiPeriod.PeriodTypeFor("Q01"));
        Assert.Throws<ArgumentException>(() => CpiPeriod.ReferenceDateFor(2026, "Q01"));
    }
}
