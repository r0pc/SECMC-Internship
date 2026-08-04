using DataIntelligence.Core.Collection;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.UnitTests.Collection;

/// <summary>
/// The validation rules that keep bad data out of the fact table (SOW 11.1).
/// </summary>
public class ScrapedRecordValidatorTests
{
    private static readonly DateTime Now = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);

    private static ScrapedRecord Valid() => new()
    {
        SourceKey = "ABC-123",
        Title = "Test item",
        PrimaryValue = 19.99m,
        Quantity = 5,
        CurrencyCode = "GBP"
    };

    [Fact]
    public void AcceptsAWellFormedRecord()
    {
        Assert.Null(ScrapedRecordValidator.Validate(Valid(), Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsBlankSourceKey(string sourceKey)
    {
        var failure = ScrapedRecordValidator.Validate(Valid() with { SourceKey = sourceKey }, Now);

        Assert.Equal(RejectionReason.MissingField, failure?.Reason);
    }

    [Fact]
    public void RejectsSourceKeyLongerThanTheColumn()
    {
        var oversized = new string('x', ScrapedRecordValidator.MaxSourceKeyLength + 1);

        var failure = ScrapedRecordValidator.Validate(Valid() with { SourceKey = oversized }, Now);

        Assert.Equal(RejectionReason.OutOfRange, failure?.Reason);
    }

    [Fact]
    public void RejectsTitleLongerThanTheColumn()
    {
        var oversized = new string('x', ScrapedRecordValidator.MaxTitleLength + 1);

        var failure = ScrapedRecordValidator.Validate(Valid() with { Title = oversized }, Now);

        Assert.Equal(RejectionReason.OutOfRange, failure?.Reason);
    }

    [Fact]
    public void RejectsNegativeQuantity()
    {
        // Mirrors CK_ItemSnapshot_Quantity: caught here as one logged rejection rather than an
        // exception that aborts the whole batch.
        var failure = ScrapedRecordValidator.Validate(Valid() with { Quantity = -1 }, Now);

        Assert.Equal(RejectionReason.OutOfRange, failure?.Reason);
    }

    [Fact]
    public void RejectsMalformedCurrencyCodeAsSchemaDrift()
    {
        var failure = ScrapedRecordValidator.Validate(Valid() with { CurrencyCode = "POUNDS" }, Now);

        Assert.Equal(RejectionReason.SchemaDrift, failure?.Reason);
    }

    [Fact]
    public void AllowsAbsentCurrencyCode()
    {
        Assert.Null(ScrapedRecordValidator.Validate(Valid() with { CurrencyCode = null }, Now));
    }

    [Fact]
    public void RejectsPublishDateBeyondTheSkewTolerance()
    {
        var failure = ScrapedRecordValidator.Validate(
            Valid() with { PublishedAtUtc = Now.AddHours(1) }, Now);

        Assert.Equal(RejectionReason.OutOfRange, failure?.Reason);
    }

    [Fact]
    public void AllowsPublishDateWithinTheSkewTolerance()
    {
        // The source's clock is not ours; a few minutes ahead is normal, not corrupt.
        Assert.Null(ScrapedRecordValidator.Validate(
            Valid() with { PublishedAtUtc = Now.AddMinutes(2) }, Now));
    }
}
