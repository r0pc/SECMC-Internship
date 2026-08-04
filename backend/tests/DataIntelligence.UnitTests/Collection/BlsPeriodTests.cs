using DataIntelligence.Core.Collection;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.UnitTests.Collection;

/// <summary>
/// BLS period tokens. Getting these wrong is not a crash — it is a silent analytical error,
/// which is exactly why they are pinned here.
/// </summary>
public class BlsPeriodTests
{
    [Theory]
    [InlineData("2026", "M01", 2026, 1, 1)]
    [InlineData("2026", "M06", 2026, 6, 1)]
    [InlineData("2026", "M12", 2026, 12, 1)]
    public void MonthlyPeriodsMapToTheFirstOfTheMonth(
        string year, string period, int y, int m, int d)
    {
        Assert.True(BlsPeriod.TryParse(year, period, out var date, out var type));

        Assert.Equal(new DateOnly(y, m, d), date);
        Assert.Equal(PeriodType.Month, type);
    }

    [Fact]
    public void M13IsTheAnnualAverage_NotAThirteenthMonth()
    {
        // The trap: treated as a month, M13 would add a phantom data point every year and drag
        // any trend line with it.
        Assert.True(BlsPeriod.TryParse("2026", "M13", out var date, out var type));

        Assert.Equal(new DateOnly(2026, 1, 1), date);
        Assert.Equal(PeriodType.Annual, type);
    }

    [Theory]
    [InlineData("S01", 1, PeriodType.Semiannual)]
    [InlineData("S02", 7, PeriodType.Semiannual)]
    public void SemiannualPeriodsMapToTheHalfTheyStart(string period, int month, PeriodType expected)
    {
        Assert.True(BlsPeriod.TryParse("2026", period, out var date, out var type));

        Assert.Equal(new DateOnly(2026, month, 1), date);
        Assert.Equal(expected, type);
    }

    [Fact]
    public void S03IsAlsoAnAnnualAverage()
    {
        Assert.True(BlsPeriod.TryParse("2026", "S03", out _, out var type));

        Assert.Equal(PeriodType.Annual, type);
    }

    [Theory]
    [InlineData("Q01", 1)]
    [InlineData("Q02", 4)]
    [InlineData("Q03", 7)]
    [InlineData("Q04", 10)]
    public void QuarterlyPeriodsMapToTheQuarterStart(string period, int month)
    {
        Assert.True(BlsPeriod.TryParse("2026", period, out var date, out var type));

        Assert.Equal(new DateOnly(2026, month, 1), date);
        Assert.Equal(PeriodType.Quarter, type);
    }

    [Theory]
    [InlineData("Q05")]
    [InlineData("A01")]
    public void AnnualTokensMapToJanuaryFirst(string period)
    {
        Assert.True(BlsPeriod.TryParse("2026", period, out var date, out var type));

        Assert.Equal(new DateOnly(2026, 1, 1), date);
        Assert.Equal(PeriodType.Annual, type);
    }

    [Theory]
    [InlineData("2026", "M00")]
    [InlineData("2026", "M14")]
    [InlineData("2026", "Z01")]
    [InlineData("2026", "M6")]
    [InlineData("2026", "")]
    [InlineData("2026", null)]
    [InlineData("not-a-year", "M06")]
    [InlineData("1800", "M06")]
    [InlineData(null, "M06")]
    public void UnrecognisedTokensAreRejectedRatherThanGuessed(string? year, string? period)
    {
        // Returning false makes an unfamiliar token a logged rejection instead of a wrong date.
        Assert.False(BlsPeriod.TryParse(year, period, out _, out _));
    }
}
