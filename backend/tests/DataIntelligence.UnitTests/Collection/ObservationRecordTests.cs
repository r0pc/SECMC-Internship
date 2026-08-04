using System.Globalization;
using DataIntelligence.Core.Collection;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.UnitTests.Collection;

/// <summary>
/// The row hash decides whether a published figure counts as revised, so it drives both FR-3
/// (no duplicate rows) and whether a genuine revision is noticed at all.
/// </summary>
public class ObservationRecordTests
{
    private static ObservationRecord Record(decimal value = 333.952m, string? annotation = null) => new()
    {
        SeriesCode = "CUUR0000SA0",
        ReferenceDate = new DateOnly(2026, 6, 1),
        PeriodType = PeriodType.Month,
        SourcePeriodCode = "M06",
        Value = value,
        SourceAnnotation = annotation
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
    public void AChangedAnnotationCountsAsARevision()
    {
        // BLS flips a footnote to "R" when a figure is revised, sometimes alongside a value it
        // has already corrected elsewhere. The transition is itself meaningful.
        Assert.NotEqual(Record().ComputeRowHash(), Record(annotation: "R").ComputeRowHash());
    }

    [Fact]
    public void AnAbsentAnnotationIsDistinctFromAnEmptyOne()
    {
        Assert.NotEqual(
            Record(annotation: null).ComputeRowHash(),
            Record(annotation: "").ComputeRowHash());
    }

    [Fact]
    public void TheHashIgnoresIdentityAndTiming()
    {
        // Only the published figure matters. Hashing the series or date would make every record
        // unique and defeat the comparison entirely.
        var a = Record() with { SeriesCode = "CUSR0000SA0", ReferenceDate = new DateOnly(2020, 1, 1) };

        Assert.Equal(Record().ComputeRowHash(), a.ComputeRowHash());
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

/// <summary>Validation rules that keep bad data out of the fact table (SOW 11.1).</summary>
public class ObservationValidatorTests
{
    private static readonly DateTime Now = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

    private static ObservationRecord Valid() => new()
    {
        SeriesCode = "SOFR",
        ReferenceDate = new DateOnly(2026, 7, 31),
        PeriodType = PeriodType.Day,
        Value = 3.66m
    };

    [Fact]
    public void AcceptsAWellFormedObservation()
    {
        Assert.Null(ObservationValidator.Validate(Valid(), Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsABlankSeriesCode(string code)
    {
        var failure = ObservationValidator.Validate(Valid() with { SeriesCode = code }, Now);

        Assert.Equal(RejectionReason.MissingField, failure?.Reason);
    }

    [Fact]
    public void RejectsASeriesCodeLongerThanTheColumn()
    {
        var oversized = new string('x', ObservationValidator.MaxSeriesCodeLength + 1);

        var failure = ObservationValidator.Validate(Valid() with { SeriesCode = oversized }, Now);

        Assert.Equal(RejectionReason.OutOfRange, failure?.Reason);
    }

    [Fact]
    public void RejectsAPeriodBeyondTheSkewTolerance()
    {
        var failure = ObservationValidator.Validate(
            Valid() with { ReferenceDate = new DateOnly(2026, 9, 1) }, Now);

        Assert.Equal(RejectionReason.OutOfRange, failure?.Reason);
    }

    [Fact]
    public void AllowsTomorrowsPeriod()
    {
        // SOFR for a given day is published early the next business day, and timezones make a
        // strict "not after today" rule reject legitimate data.
        Assert.Null(ObservationValidator.Validate(
            Valid() with { ReferenceDate = new DateOnly(2026, 8, 5) }, Now));
    }

    [Fact]
    public void RejectsAPeriodBeforeAnyPublishedData()
    {
        var failure = ObservationValidator.Validate(
            Valid() with { ReferenceDate = new DateOnly(1850, 1, 1) }, Now);

        Assert.Equal(RejectionReason.OutOfRange, failure?.Reason);
    }
}
