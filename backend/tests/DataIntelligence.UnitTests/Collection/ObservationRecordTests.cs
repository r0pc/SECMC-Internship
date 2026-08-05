using System.Globalization;
using DataIntelligence.Core.Collection;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.UnitTests.Collection;

/// <summary>
/// The row hash decides whether a published figure counts as revised, so it drives both FR-3
/// (no duplicate rows) and whether a genuine revision is noticed at all.
/// </summary>
public class CpiObservationRecordTests
{
    private static CpiObservationRecord Record(decimal value = 333.952m, string? footnotes = null) => new()
    {
        ReferenceYear = 2026,
        PeriodCode = "M06",
        PeriodType = PeriodType.Month,
        ReferenceDate = new DateOnly(2026, 6, 1),
        IndexValue = value,
        Footnotes = footnotes
    };

    [Fact]
    public void IdenticalObservationsHashEqually()
    {
        Assert.Equal(Record().ComputeRowHash(), Record().ComputeRowHash());
    }

    [Fact]
    public void AChangedValueChangesTheHash()
    {
        Assert.NotEqual(Record().ComputeRowHash(), Record(334.100m).ComputeRowHash());
    }

    [Fact]
    public void ATrailingZeroDoesNotCountAsARevision()
    {
        // 333.95 and 333.950 are the same number. Decimal keeps the scale, so this would
        // otherwise register as a revision every time BLS changed its formatting.
        Assert.Equal(
            Record(333.95m).ComputeRowHash(),
            Record(333.950m).ComputeRowHash());
    }

    [Fact]
    public void AChangedFootnoteCountsAsARevision()
    {
        // BLS flips a footnote to "R" when a figure is revised, sometimes alongside a value it
        // has already corrected elsewhere. The transition is itself meaningful.
        Assert.NotEqual(Record().ComputeRowHash(), Record(footnotes: "R").ComputeRowHash());
    }

    [Fact]
    public void AnAbsentFootnoteIsDistinctFromAnEmptyOne()
    {
        Assert.NotEqual(
            Record(footnotes: null).ComputeRowHash(),
            Record(footnotes: "").ComputeRowHash());
    }

    [Fact]
    public void TheHashIgnoresIdentityAndTiming()
    {
        // Only the published figure matters. The period is the key the hash is compared under,
        // not part of what is being compared; hashing it would make every record unique and
        // defeat the comparison entirely.
        var other = Record() with
        {
            ReferenceYear = 2020,
            PeriodCode = "M01",
            ReferenceDate = new DateOnly(2020, 1, 1)
        };

        Assert.Equal(Record().ComputeRowHash(), other.ComputeRowHash());
    }

    [Fact]
    public void TheHashIsCultureIndependent()
    {
        // A host set to a comma-decimal locale must agree with one that is not, or a failover
        // would rewrite every observation as revised.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var invariant = Record().ComputeRowHash();

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(invariant, Record().ComputeRowHash());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void TheHashIsThirtyTwoBytes()
    {
        // binary(32) in the schema; anything longer would be silently truncated by SQL Server.
        Assert.Equal(32, Record().ComputeRowHash().Length);
    }
}

/// <summary>A SOFR day's hash covers every measure the publisher gave, not just the rate.</summary>
public class SofrDailyRateRecordTests
{
    private static SofrDailyRateRecord Record() => new()
    {
        EffectiveDate = new DateOnly(2026, 8, 3),
        RatePercent = 3.65m,
        Percentile1Percent = 3.61m,
        Percentile25Percent = 3.63m,
        Percentile75Percent = 3.70m,
        Percentile99Percent = 3.73m,
        VolumeUsdBillions = 3055m
    };

    [Fact]
    public void IdenticalDaysHashEqually()
    {
        Assert.Equal(Record().ComputeRowHash(), Record().ComputeRowHash());
    }

    [Fact]
    public void ACorrectedVolumeCountsAsARevision()
    {
        // The reason every measure is hashed. A restatement that moved only the volume is still a
        // restatement; hashing the rate alone would file it as "unchanged" and lose it.
        Assert.NotEqual(
            Record().ComputeRowHash(),
            (Record() with { VolumeUsdBillions = 3060m }).ComputeRowHash());
    }

    [Fact]
    public void ACorrectedPercentileCountsAsARevision()
    {
        Assert.NotEqual(
            Record().ComputeRowHash(),
            (Record() with { Percentile25Percent = 3.64m }).ComputeRowHash());
    }

    [Fact]
    public void AnAbsentMeasureIsDistinctFromZero()
    {
        Assert.NotEqual(
            (Record() with { VolumeUsdBillions = null }).ComputeRowHash(),
            (Record() with { VolumeUsdBillions = 0m }).ComputeRowHash());
    }

    [Fact]
    public void TheRevisionIndicatorIsPartOfTheHash()
    {
        // The NY Fed can set the indicator on a day whose numbers it has already corrected
        // elsewhere; the flag turning on is itself the news.
        Assert.NotEqual(
            Record().ComputeRowHash(),
            (Record() with { RevisionIndicator = "Y" }).ComputeRowHash());
    }

    [Fact]
    public void TheHashIgnoresTheDate()
    {
        Assert.Equal(
            Record().ComputeRowHash(),
            (Record() with { EffectiveDate = new DateOnly(2020, 1, 2) }).ComputeRowHash());
    }
}

/// <summary>
/// Validation rules that keep bad data out of the fact tables (SOW 11.1). Each mirrors a CHECK
/// constraint, so a violation costs one logged rejection instead of aborting the whole batch.
/// </summary>
public class ObservationValidatorTests
{
    private static readonly DateTime Now = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

    private static CpiObservationRecord Cpi() => new()
    {
        ReferenceYear = 2026,
        PeriodCode = "M06",
        PeriodType = PeriodType.Month,
        ReferenceDate = new DateOnly(2026, 6, 1),
        IndexValue = 333.952m
    };

    private static SofrDailyRateRecord Sofr() => new()
    {
        EffectiveDate = new DateOnly(2026, 7, 31),
        RatePercent = 3.66m,
        Percentile1Percent = 3.60m,
        Percentile25Percent = 3.64m,
        Percentile75Percent = 3.72m,
        Percentile99Percent = 3.75m,
        VolumeUsdBillions = 3205m
    };

    [Fact]
    public void AcceptsAWellFormedCpiFigure() =>
        Assert.Null(ObservationValidator.Validate(Cpi(), Now));

    [Fact]
    public void AcceptsAWellFormedSofrDay() =>
        Assert.Null(ObservationValidator.Validate(Sofr(), Now));

    [Fact]
    public void AcceptsTheAnnualAverageAsAnAnnualPeriod()
    {
        var annual = Cpi() with
        {
            PeriodCode = "M13",
            PeriodType = PeriodType.Annual,
            ReferenceDate = new DateOnly(2026, 1, 1)
        };

        Assert.Null(ObservationValidator.Validate(annual, Now));
    }

    [Fact]
    public void RejectsTheAnnualAverageMislabelledAsAMonth()
    {
        // The failure this rule exists for: M13 filed as a month would be charted as a thirteenth
        // month and averaged into the year it summarises.
        var mislabelled = Cpi() with
        {
            PeriodCode = "M13",
            PeriodType = PeriodType.Month,
            ReferenceDate = new DateOnly(2026, 1, 1)
        };

        Assert.Equal(RejectionReason.SchemaDrift,
            ObservationValidator.Validate(mislabelled, Now)?.Reason);
    }

    [Theory]
    [InlineData("Q01")]
    [InlineData("A01")]
    [InlineData("M14")]
    [InlineData("")]
    public void RejectsAPeriodCodeOutsideTheStoredSet(string periodCode)
    {
        var failure = ObservationValidator.Validate(Cpi() with { PeriodCode = periodCode }, Now);

        Assert.Equal(RejectionReason.UnparseablePeriod, failure?.Reason);
    }

    [Fact]
    public void RejectsAReferenceDateThatDisagreesWithItsPeriod()
    {
        var mismatched = Cpi() with { ReferenceDate = new DateOnly(2026, 3, 1) };

        Assert.Equal(RejectionReason.UnparseablePeriod,
            ObservationValidator.Validate(mismatched, Now)?.Reason);
    }

    [Fact]
    public void RejectsAYearBeforeAnyPublishedCpi()
    {
        var ancient = Cpi() with
        {
            ReferenceYear = 1850,
            ReferenceDate = new DateOnly(1850, 6, 1)
        };

        Assert.Equal(RejectionReason.OutOfRange,
            ObservationValidator.Validate(ancient, Now)?.Reason);
    }

    [Fact]
    public void RejectsANonPositiveIndexValue()
    {
        // An index is a ratio against a base period; zero is not a low reading, it is a misparse.
        Assert.Equal(RejectionReason.OutOfRange,
            ObservationValidator.Validate(Cpi() with { IndexValue = 0m }, Now)?.Reason);
    }

    [Fact]
    public void RejectsACpiPeriodBeyondTheSkewTolerance()
    {
        var future = Cpi() with
        {
            PeriodCode = "M10",
            ReferenceDate = new DateOnly(2026, 10, 1)
        };

        Assert.Equal(RejectionReason.OutOfRange,
            ObservationValidator.Validate(future, Now)?.Reason);
    }

    [Fact]
    public void AllowsTomorrowsSofrDay()
    {
        // SOFR for a given day is published early the next business day, and timezones make a
        // strict "not after today" rule reject legitimate data.
        Assert.Null(ObservationValidator.Validate(
            Sofr() with { EffectiveDate = new DateOnly(2026, 8, 5) }, Now));
    }

    [Fact]
    public void RejectsARateWithTheDecimalPointInTheWrongPlace()
    {
        // 3.65 read as 365: the failure the rate band exists to catch, and the one that would
        // otherwise put a chart out by two orders of magnitude.
        Assert.Equal(RejectionReason.OutOfRange,
            ObservationValidator.Validate(Sofr() with { RatePercent = 365m }, Now)?.Reason);
    }

    [Fact]
    public void RejectsPercentilesInTheWrongOrder()
    {
        // Out of order means the fields were mapped to the wrong columns, which nothing
        // downstream could recover from.
        var reversed = Sofr() with
        {
            Percentile1Percent = 3.75m,
            Percentile99Percent = 3.60m
        };

        Assert.Equal(RejectionReason.SchemaDrift,
            ObservationValidator.Validate(reversed, Now)?.Reason);
    }

    [Fact]
    public void AllowsAMissingPercentile()
    {
        // Absent on a low-volume day is a missing measure, not a broken record.
        Assert.Null(ObservationValidator.Validate(
            Sofr() with { Percentile1Percent = null }, Now));
    }

    [Fact]
    public void RejectsANegativeVolume()
    {
        Assert.Equal(RejectionReason.OutOfRange,
            ObservationValidator.Validate(Sofr() with { VolumeUsdBillions = -1m }, Now)?.Reason);
    }

    [Fact]
    public void RejectsAnUnrecognisedRevisionIndicator()
    {
        Assert.Equal(RejectionReason.TypeMismatch,
            ObservationValidator.Validate(Sofr() with { RevisionIndicator = "Maybe" }, Now)?.Reason);
    }
}
