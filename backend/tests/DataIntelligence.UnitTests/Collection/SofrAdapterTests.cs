using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Exceptions;
using DataIntelligence.Infrastructure.Collection;
using Microsoft.Extensions.Options;

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
            "volumeInBillions": 3011, "revisionIndicator": "R" }
        ] }
        """;

    private static SofrAdapter CreateAdapter(int lookback = 10) =>
        new(Options.Create(new CollectionOptions
        {
            Sofr = new SofrOptions { LookbackBusinessDays = lookback }
        }));

    [Fact]
    public void BuildRequest_IsAGetWithTheConfiguredLookback()
    {
        var request = CreateAdapter(lookback: 5).BuildRequest(new SourceRequestContextBuilder().Build());

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains("/sofr/last/5.json", request.Url);
        Assert.Null(request.JsonBody);
    }

    [Fact]
    public void Parse_SplitsOneRecordIntoSixSeries()
    {
        // The central modelling decision: one API record carries six measures, and each becomes
        // its own observation so the fact table stays (series, date) -> value.
        var result = CreateAdapter().Parse(SamplePayload);

        Assert.Equal(2, result.EntriesSeen);
        Assert.Empty(result.Rejections);
        Assert.Equal(12, result.Records.Count);

        var codes = result.Records
            .Where(r => r.ReferenceDate == new DateOnly(2026, 7, 31))
            .Select(r => r.SeriesCode)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["SOFR", "SOFR_P1", "SOFR_P25", "SOFR_P75", "SOFR_P99", "SOFR_VOL"],
            codes);
    }

    [Fact]
    public void Parse_ReadsRateVolumeAndPercentiles()
    {
        var day = CreateAdapter().Parse(SamplePayload).Records
            .Where(r => r.ReferenceDate == new DateOnly(2026, 7, 31))
            .ToDictionary(r => r.SeriesCode, r => r.Value);

        Assert.Equal(3.66m, day["SOFR"]);
        Assert.Equal(3205m, day["SOFR_VOL"]);
        Assert.Equal(3.60m, day["SOFR_P1"]);
        Assert.Equal(3.75m, day["SOFR_P99"]);
    }

    [Fact]
    public void Parse_TreatsEveryMeasureAsADailyObservation()
    {
        var record = CreateAdapter().Parse(SamplePayload).Records.First();

        Assert.Equal(PeriodType.Day, record.PeriodType);
    }

    [Fact]
    public void Parse_CarriesTheRevisionIndicator()
    {
        var records = CreateAdapter().Parse(SamplePayload).Records;

        // Empty string means "not revised" and must become null, or every unrevised day would
        // hash differently from one that genuinely has no annotation.
        Assert.Null(records.First(r => r.ReferenceDate == new DateOnly(2026, 7, 31)).SourceAnnotation);
        Assert.Equal("R", records.First(r => r.ReferenceDate == new DateOnly(2026, 7, 30)).SourceAnnotation);
    }

    [Fact]
    public void Parse_SkipsAnAbsentPercentileWithoutLosingTheRest()
    {
        // Percentiles are occasionally absent on low-volume days; that is a missing measure,
        // not a broken record.
        const string body = """
            { "refRates": [ { "effectiveDate": "2026-07-31", "type": "SOFR",
              "percentRate": 3.66, "volumeInBillions": 3205, "percentPercentile1": null } ] }
            """;

        var result = CreateAdapter().Parse(body);

        Assert.Equal(2, result.Records.Count);
        Assert.Contains(result.Records, r => r.SeriesCode == "SOFR");
        Assert.DoesNotContain(result.Records, r => r.SeriesCode == "SOFR_P1");
    }

    [Fact]
    public void Parse_RejectsRecordsForOtherSecuredRates()
    {
        // The payload is shared with BGCR and TGCR. Filtering defensively stops a change
        // upstream quietly filing another rate's values against SOFR's series.
        const string body = """
            { "refRates": [ { "effectiveDate": "2026-07-31", "type": "BGCR", "percentRate": 3.60 } ] }
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

        Assert.Equal(3.66m, CreateAdapter().Parse(body).Records.Single().Value);
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
