using DataIntelligence.Worker;

namespace DataIntelligence.UnitTests.Collection;

/// <summary>
/// How the Worker reads its command line. Mostly refusals — and a refusal that quietly stops
/// refusing is exactly what a manual check does not notice.
/// </summary>
public class WorkerCommandLineTests
{
    private static readonly DateTime Now = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

    private static WorkerRunMode Parse(params string[] args)
    {
        Assert.True(WorkerCommandLine.TryParse(args, Now, out var mode, out var error),
            $"Expected these arguments to parse, but: {error}");

        return mode;
    }

    private static string Reject(params string[] args)
    {
        Assert.False(WorkerCommandLine.TryParse(args, Now, out _, out var error),
            "Expected these arguments to be refused.");

        Assert.NotNull(error);
        return error!;
    }

    [Fact]
    public void NoArguments_RunsOnTheSchedule()
    {
        var mode = Parse();

        Assert.Equal(WorkerMode.Scheduled, mode.Mode);
        Assert.False(mode.IsOneShot);
    }

    [Fact]
    public void Once_CollectsEverythingAndExits()
    {
        var mode = Parse("--once");

        Assert.Equal(WorkerMode.Once, mode.Mode);
        Assert.True(mode.IsOneShot);
    }

    [Fact]
    public void Backfill_CoversBothDatasets()
    {
        var mode = Parse("--backfill");

        Assert.Equal(WorkerMode.Backfill, mode.Mode);
        Assert.True(mode.IncludeCpi);
        Assert.True(mode.IncludeSofr);
        Assert.Equal(WorkerRunMode.EarliestCpiYear, mode.CpiFromYear);
    }

    [Fact]
    public void BackfillCpi_CoversCpiAlone()
    {
        var mode = Parse("--backfill-cpi");

        Assert.True(mode.IncludeCpi);
        Assert.False(mode.IncludeSofr);
    }

    [Fact]
    public void BackfillSofr_CoversSofrAlone()
    {
        var mode = Parse("--backfill-sofr");

        Assert.False(mode.IncludeCpi);
        Assert.True(mode.IncludeSofr);
    }

    [Fact]
    public void TheTwoNarrowFlagsTogetherMeanTheSameAsTheBroadOne()
    {
        var combined = Parse("--backfill-cpi", "--backfill-sofr");

        Assert.True(combined.IncludeCpi);
        Assert.True(combined.IncludeSofr);
    }

    [Fact]
    public void BackfillCpiIsNotMistakenForBackfill()
    {
        // The flags share a prefix, so a StartsWith comparison would make --backfill-cpi turn on
        // the SOFR half too, and quietly collect eight years nobody asked for.
        Assert.False(Parse("--backfill-cpi").IncludeSofr);
    }

    [Theory]
    [InlineData("--from", "2000")]
    [InlineData("--from=2000")]
    public void From_SetsTheFirstCpiYear_InEitherSpelling(params string[] fromArgs)
    {
        var mode = Parse(["--backfill", .. fromArgs]);

        Assert.Equal(2000, mode.CpiFromYear);
    }

    [Fact]
    public void From_AppliesToTheCpiHalfOfACombinedBackfill()
    {
        var mode = Parse("--backfill", "--from", "2015");

        Assert.True(mode.IncludeCpi);
        Assert.True(mode.IncludeSofr);
        Assert.Equal(2015, mode.CpiFromYear);
    }

    [Fact]
    public void Once_AndBackfill_AreRefusedTogether() =>
        Assert.Contains("pass one or the other", Reject("--once", "--backfill"));

    [Fact]
    public void From_WithoutABackfill_IsRefusedRatherThanIgnored()
    {
        // Silently ignoring it would leave a run that looked like it had honoured the year.
        Assert.Contains("--from applies to", Reject("--from", "2000"));
    }

    [Fact]
    public void From_WithSofrOnly_IsRefused()
    {
        // SOFR has one start date and no chunking, so there is nothing for --from to choose.
        Assert.Contains("does not collect CPI", Reject("--backfill-sofr", "--from", "2000"));
    }

    [Theory]
    [InlineData("1912")]
    [InlineData("2027")]
    public void From_OutsideThePublishedRange_IsRefused(string year) =>
        Assert.Contains("must be between", Reject("--backfill", "--from", year));

    [Fact]
    public void From_AtEitherBoundary_IsAccepted()
    {
        Assert.Equal(1913, Parse("--backfill", "--from", "1913").CpiFromYear);
        Assert.Equal(2026, Parse("--backfill", "--from", "2026").CpiFromYear);
    }

    [Theory]
    [InlineData("nineteen-ninety")]
    [InlineData("-2000")]
    [InlineData("20.00")]
    public void From_WithSomethingThatIsNotAYear_IsRefused(string value) =>
        Assert.Contains("four-digit year", Reject("--backfill", "--from", value));

    [Fact]
    public void From_WithNothingAfterIt_IsRefused() =>
        Assert.Contains("four-digit year", Reject("--backfill", "--from"));

    [Fact]
    public void FlagsAreCaseInsensitive()
    {
        Assert.True(Parse("--Backfill-CPI").IncludeCpi);
        Assert.Equal(WorkerMode.Once, Parse("--ONCE").Mode);
    }
}
