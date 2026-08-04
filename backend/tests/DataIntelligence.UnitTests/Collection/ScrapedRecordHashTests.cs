using System.Globalization;
using DataIntelligence.Core.Dtos;

namespace DataIntelligence.UnitTests.Collection;

/// <summary>
/// The row hash decides whether an observation counts as a change, so it drives both FR-3
/// (no duplicate rows) and the size of the fact table. These are the properties it must hold.
/// </summary>
public class ScrapedRecordHashTests
{
    private static ScrapedRecord Record(
        decimal? primary = 19.99m,
        int? quantity = 5,
        string? status = "InStock",
        IReadOnlyDictionary<string, string>? extras = null) =>
        new()
        {
            SourceKey = "ABC-123",
            Title = "Test item",
            PrimaryValue = primary,
            Quantity = quantity,
            StatusText = status,
            ExtraAttributes = extras ?? new Dictionary<string, string>()
        };

    [Fact]
    public void IdenticalRecordsHashEqually()
    {
        Assert.Equal(Record().ComputeRowHash(), Record().ComputeRowHash());
    }

    [Fact]
    public void ChangedMeasureChangesTheHash()
    {
        Assert.NotEqual(Record().ComputeRowHash(), Record(primary: 24.50m).ComputeRowHash());
    }

    [Fact]
    public void DescriptiveFieldsDoNotAffectTheHash()
    {
        // Title lives on the item and is updated in place; a retitled item is not a new
        // observation, so it must not spend a row in the fact table.
        var a = Record() with { Title = "Original" };
        var b = Record() with { Title = "Renamed" };

        Assert.Equal(a.ComputeRowHash(), b.ComputeRowHash());
    }

    [Fact]
    public void ExtraAttributeOrderDoesNotAffectTheHash()
    {
        // A parser that emits fields in a different order would otherwise make every record
        // look changed on every cycle.
        var forward = Record(extras: new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });
        var reverse = Record(extras: new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" });

        Assert.Equal(forward.ComputeRowHash(), reverse.ComputeRowHash());
    }

    [Fact]
    public void ExtraAttributeValueChangesTheHash()
    {
        var before = Record(extras: new Dictionary<string, string> { ["colour"] = "red" });
        var after = Record(extras: new Dictionary<string, string> { ["colour"] = "blue" });

        Assert.NotEqual(before.ComputeRowHash(), after.ComputeRowHash());
    }

    [Fact]
    public void FieldBoundariesCannotBeForged()
    {
        // Without a delimiter that cannot occur in scraped text, ("ab", "c") and ("a", "bc")
        // would hash the same and a real change could be missed.
        var first = Record(status: "ab", extras: new Dictionary<string, string> { ["x"] = "c" });
        var second = Record(status: "a", extras: new Dictionary<string, string> { ["x"] = "bc" });

        Assert.NotEqual(first.ComputeRowHash(), second.ComputeRowHash());
    }

    [Fact]
    public void NullIsDistinguishableFromEmpty()
    {
        Assert.NotEqual(
            Record(status: null).ComputeRowHash(),
            Record(status: "").ComputeRowHash());
    }

    [Fact]
    public void HashIsCultureIndependent()
    {
        // A worker host set to a comma-decimal locale must agree with one that is not, or a
        // failover between hosts would rewrite the entire catalogue as "changed".
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");
            var invariantHash = Record().ComputeRowHash();

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var germanHash = Record().ComputeRowHash();

            Assert.Equal(invariantHash, germanHash);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void HashIsThirtyTwoBytes()
    {
        // binary(32) in the schema; a longer hash would be silently truncated by SQL Server.
        Assert.Equal(32, Record().ComputeRowHash().Length);
    }
}
