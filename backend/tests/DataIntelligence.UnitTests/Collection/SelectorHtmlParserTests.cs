using DataIntelligence.Core.Enums;
using DataIntelligence.Infrastructure.Collection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataIntelligence.UnitTests.Collection;

/// <summary>
/// Covers the extraction rules the collector depends on (SOW 11.1 — data validation).
/// </summary>
public class SelectorHtmlParserTests
{
    private const string SampleHtml = """
        <html><body>
          <div class="listing" data-id="ABC-123">
            <h3>  First   item </h3>
            <span class="price">£19.99</span>
            <span class="was">£24.50</span>
            <span class="stock">12 in stock</span>
            <span class="cat">widgets</span>
            <a href="/items/abc-123">details</a>
          </div>
          <div class="listing" data-id="DEF-456">
            <h3>Second item</h3>
            <span class="price">£5.00</span>
            <span class="cat">gadgets</span>
          </div>
        </body></html>
        """;

    private static SelectorHtmlParser CreateParser(Action<ParserOptions>? customise = null)
    {
        var parser = new ParserOptions
        {
            RecordSelector = "//div[@class='listing']",
            Fields = new Dictionary<string, FieldSelector>(StringComparer.OrdinalIgnoreCase)
            {
                ["SourceKey"] = new() { Selector = ".", Attribute = "data-id", Required = true },
                ["Title"] = new() { Selector = ".//h3", Required = true },
                ["CategoryCode"] = new() { Selector = ".//span[@class='cat']" },
                ["SourceUrl"] = new() { Selector = ".//a", Attribute = "href" },
                ["PrimaryValue"] = new() { Selector = ".//span[@class='price']", Type = FieldType.Decimal, StripCharacters = "£," },
                ["SecondaryValue"] = new() { Selector = ".//span[@class='was']", Type = FieldType.Decimal, StripCharacters = "£," },
                ["Quantity"] = new() { Selector = ".//span[@class='stock']", Type = FieldType.Integer, ExtractPattern = @"(\d+)" }
            }
        };

        customise?.Invoke(parser);

        var options = Options.Create(new CollectionOptions { Parser = parser });
        return new SelectorHtmlParser(options, NullLogger<SelectorHtmlParser>.Instance);
    }

    [Fact]
    public void Parse_ExtractsEveryConfiguredField()
    {
        var result = CreateParser().Parse(SampleHtml);

        Assert.Equal(2, result.RecordNodesMatched);
        Assert.Empty(result.Rejections);

        var first = result.Records[0];
        Assert.Equal("ABC-123", first.SourceKey);
        Assert.Equal(19.99m, first.PrimaryValue);
        Assert.Equal(24.50m, first.SecondaryValue);
        Assert.Equal(12, first.Quantity);
        Assert.Equal("widgets", first.CategoryCode);
        Assert.Equal("/items/abc-123", first.SourceUrl);
    }

    [Fact]
    public void Parse_CollapsesWhitespaceInText()
    {
        // Otherwise a source that reindents its HTML makes every record look changed.
        var result = CreateParser().Parse(SampleHtml);

        Assert.Equal("First item", result.Records[0].Title);
    }

    [Fact]
    public void Parse_LeavesOptionalUnmatchedFieldsNull()
    {
        var result = CreateParser().Parse(SampleHtml);

        var second = result.Records[1];
        Assert.Null(second.SecondaryValue);
        Assert.Null(second.Quantity);
        Assert.Equal(5.00m, second.PrimaryValue);
    }

    [Fact]
    public void Parse_ReportsZeroMatches_WhenSelectorFindsNothing()
    {
        // The layout-change signature: the document is fine, the selector is stale. Reported as
        // a count so the runner can tell it apart from a genuinely empty result set.
        var result = CreateParser(p => p.RecordSelector = "//div[@class='does-not-exist']")
            .Parse(SampleHtml);

        Assert.Equal(0, result.RecordNodesMatched);
        Assert.Empty(result.Records);
    }

    [Fact]
    public void Parse_RejectsRecord_WhenRequiredFieldIsMissing()
    {
        var html = """
            <div class="listing" data-id="NO-TITLE"><span class="price">£1.00</span></div>
            """;

        var result = CreateParser().Parse(html);

        Assert.Empty(result.Records);
        var rejection = Assert.Single(result.Rejections);
        Assert.Equal(RejectionReason.MissingField, rejection.Reason);
    }

    [Fact]
    public void Parse_RejectsRecord_WhenNumberCannotBeParsed()
    {
        var html = """
            <div class="listing" data-id="BAD"><h3>Bad</h3><span class="price">call us</span></div>
            """;

        var result = CreateParser().Parse(html);

        Assert.Empty(result.Records);
        var rejection = Assert.Single(result.Rejections);
        Assert.Equal(RejectionReason.TypeMismatch, rejection.Reason);
        Assert.Equal("BAD", rejection.SourceKey);
    }

    [Fact]
    public void Parse_RejectsSecondRecord_WhenSourceKeyRepeatsInOnePayload()
    {
        // Caught here rather than surfacing as a unique-index violation at save time.
        var html = """
            <div class="listing" data-id="SAME"><h3>One</h3></div>
            <div class="listing" data-id="SAME"><h3>Two</h3></div>
            """;

        var result = CreateParser().Parse(html);

        Assert.Single(result.Records);
        var rejection = Assert.Single(result.Rejections);
        Assert.Equal(RejectionReason.DuplicateKey, rejection.Reason);
    }

    [Fact]
    public void Parse_StoresUnknownFieldsAsExtensionAttributes()
    {
        var result = CreateParser(p =>
            p.Fields["Vendor"] = new FieldSelector { Selector = ".//span[@class='cat']" })
            .Parse(SampleHtml);

        Assert.Equal("widgets", result.Records[0].ExtraAttributes["Vendor"]);
    }

    [Fact]
    public void Parse_MatchesFieldNamesCaseInsensitively()
    {
        // A profile written in lower case must still map to the real columns, not silently
        // become extension attributes.
        var result = CreateParser(p =>
        {
            p.Fields.Remove("PrimaryValue");
            p.Fields["primaryvalue"] = new FieldSelector
            {
                Selector = ".//span[@class='price']", Type = FieldType.Decimal, StripCharacters = "£,"
            };
        }).Parse(SampleHtml);

        Assert.Equal(19.99m, result.Records[0].PrimaryValue);
        Assert.Empty(result.Records[0].ExtraAttributes);
    }

    [Fact]
    public void Parse_Throws_WhenNoRecordSelectorIsConfigured()
    {
        var parser = CreateParser(p => p.RecordSelector = "");

        var ex = Assert.Throws<Core.Exceptions.CollectionFailureException>(() => parser.Parse(SampleHtml));
        Assert.Equal(CollectionFailureCategory.ParseError, ex.Category);
    }
}
