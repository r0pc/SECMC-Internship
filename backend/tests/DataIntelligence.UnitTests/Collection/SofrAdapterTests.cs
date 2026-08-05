using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Exceptions;
using DataIntelligence.Infrastructure.Collection;

namespace DataIntelligence.UnitTests.Collection;

/// <summary>
/// SOFR adapter behaviour (SOW 11.1). The sample is the real payload shape returned by
/// markets.newyorkfed.org, including its <c>revisionIndicator</c> field.
/// </summary>
public class SofrAdapterTests
{
    private const string SamplePayload = """
        { "refRates": [
          { "effectiveDate": "2026-07-31", "type": "SOFR", "percentRate": 3.66,
            "percentPercentile1": 3.60, "percentPercentile25": 3.64,
            "percentPercentile75": 3.72, "percentPercentile99": 3.75,
            "volumeInBillions": 3205, "revisionIndicator": "" },
          { "effectiveDate": "2026-07-30", "type": "SOFR", "percentRate": 3.65,
            "percentPercentile1": 3.60, "percentPercentile25": 3.63,
            "percentPercentile75": 3.70, "percentPercentile99": 3.73,
            "volumeInBillions": 3011, "revisionIndicator": "Y" }
        ] }
        """;

    private static SofrAdapter CreateAdapter() => new();

    private static SofrDailyRateRecord Day(ParseResult result, int year, int month, int day) =>
        result.Records.OfType<SofrDailyRateRecord>()
            .Single(r => r.EffectiveDate == new DateOnly(year, month, day));

    [Fact]
    public void BuildRequest_AsksForTheCurrentCalendarYear()
    {
        // The annual extract the schema is written against: 1 January to today, every cycle, so
        // an outage or a late revision is repaired by the next run rather than persisting.
        var context = new SourceRequestContextBuilder()
            .At(new DateTime(2026, 8, 4, 10, 0, 0, DateTimeKind.Utc))
            .Build();

        var request = CreateAdapter().BuildRequest(context);

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains("/sofr/search.json", request.Url);
        Assert.Contains("startDate=2026-01-01", request.Url);
        Assert.Contains("endDate=2026-08-04", request.Url);
        Assert.Null(request.JsonBody);
    }

    [Fact]
    public void Parse_TurnsOneRecordIntoOneRow()
    {
        // The central modelling decision, and the one that changed: a business day's six measures
        // are columns of one row, not six rows.
        var result = CreateAdapter().Parse(SamplePayload);

        Assert.Equal(2, result.EntriesSeen);
        Assert.Empty(result.Rejections);
        Assert.Equal(2, result.Records.Count);
    }

    [Fact]
    public void Parse_ReadsRateVolumeAndPercentiles()
    {
        var day = Day(CreateAdapter().Parse(SamplePayload), 2026, 7, 31);

        Assert.Equal(3.66m, day.RatePercent);
        Assert.Equal(3205m, day.VolumeUsdBillions);
        Assert.Equal(3.60m, day.Percentile1Percent);
        Assert.Equal(3.64m, day.Percentile25Percent);
        Assert.Equal(3.72m, day.Percentile75Percent);
        Assert.Equal(3.75m, day.Percentile99Percent);
    }

    [Fact]
    public void Parse_CarriesTheRevisionIndicator()
    {
        var result = CreateAdapter().Parse(SamplePayload);

        // Empty string means "not revised" and must become null, or every unrevised day would
        // hash differently from one that genuinely has no annotation.
        Assert.Null(Day(result, 2026, 7, 31).RevisionIndicator);
        Assert.Equal("Y", Day(result, 2026, 7, 30).RevisionIndicator);
    }

    [Fact]
    public void Parse_KeepsTheDayWhenAPercentileIsAbsent()
    {
        // Percentiles are occasionally absent on low-volume days; that is a missing measure, not
        // a broken record, so the day still lands with the rest of its measures.
        const string body = """
            { "refRates": [ { "effectiveDate": "2026-07-31", "type": "SOFR",
              "percentRate": 3.66, "volumeInBillions": 3205, "percentPercentile1": null } ] }
            """;

        var day = Day(CreateAdapter().Parse(body), 2026, 7, 31);

        Assert.Equal(3.66m, day.RatePercent);
        Assert.Equal(3205m, day.VolumeUsdBillions);
        Assert.Null(day.Percentile1Percent);
    }

    [Fact]
    public void Parse_RejectsADayWithNoRate()
    {
        // The one measure a row cannot do without: everything else on it describes the rate.
        const string body = """
            { "refRates": [ { "effectiveDate": "2026-07-31", "type": "SOFR",
              "volumeInBillions": 3205 } ] }
            """;

        var result = CreateAdapter().Parse(body);

        Assert.Empty(result.Records);
        Assert.Equal(RejectionReason.MissingField, result.Rejections.Single().Reason);
    }

    [Theory]
    [InlineData("EFFR")]
    [InlineData("OBFR")]
    [InlineData("TGCR")]
    [InlineData("BGCR")]
    public void Parse_RejectsTheOtherRatesInTheSamePayload(string rateType)
    {
        // Four of these arrive every business day and are out of scope. Rejecting them
        // explicitly means the exclusion is visible in core.RejectedObservation rather than
        // invisible in a filter — and stops a change upstream filing another rate as SOFR.
        var body = $$"""
            { "refRates": [ { "effectiveDate": "2026-07-31", "type": "{{rateType}}",
              "percentRate": 3.63 } ] }
            """;

        var result = CreateAdapter().Parse(body);

        Assert.Empty(result.Records);
        Assert.Equal(RejectionReason.UnknownSeries, result.Rejections.Single().Reason);
    }

    [Fact]
    public void Parse_RejectsAnUnparseableEffectiveDate()
    {
        const string body = """
            { "refRates": [ { "effectiveDate": "31/07/2026", "type": "SOFR", "percentRate": 3.66 } ] }
            """;

        var result = CreateAdapter().Parse(body);

        Assert.Equal(RejectionReason.UnparseablePeriod, result.Rejections.Single().Reason);
    }

    [Fact]
    public void Parse_RejectsARepeatedDateWithinOnePayload()
    {
        const string body = """
            { "refRates": [
              { "effectiveDate": "2026-07-31", "type": "SOFR", "percentRate": 3.66 },
              { "effectiveDate": "2026-07-31", "type": "SOFR", "percentRate": 3.70 } ] }
            """;

        var result = CreateAdapter().Parse(body);

        Assert.Single(result.Records);
        Assert.Equal(RejectionReason.DuplicatePeriod, result.Rejections.Single().Reason);
    }

    [Fact]
    public void Parse_AcceptsNumericStrings()
    {
        // The API has served both JSON numbers and quoted numerics.
        const string body = """
            { "refRates": [ { "effectiveDate": "2026-07-31", "type": "SOFR", "percentRate": "3.66" } ] }
            """;

        Assert.Equal(3.66m, Day(CreateAdapter().Parse(body), 2026, 7, 31).RatePercent);
    }

    [Fact]
    public void Parse_Throws_WhenTheContractChanges()
    {
        var ex = Assert.Throws<CollectionFailureException>(() => CreateAdapter().Parse("""{"rates":[]}"""));

        Assert.Equal(CollectionFailureCategory.SchemaChanged, ex.Category);
    }

    [Fact]
    public void Parse_Throws_OnInvalidJson()
    {
        var ex = Assert.Throws<CollectionFailureException>(() => CreateAdapter().Parse("not json"));

        Assert.Equal(CollectionFailureCategory.ParseError, ex.Category);
    }
}
