using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Exceptions;
using DataIntelligence.Infrastructure.Collection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DataIntelligence.UnitTests.Collection;

/// <summary>
/// BLS adapter behaviour (SOW 11.1). The sample payload is the real shape returned by
/// api.bls.gov, including its string-typed values and <c>[{}]</c> empty footnote array.
/// </summary>
public class BlsCpiAdapterTests
{
    private const string SamplePayload = """
        {"status":"REQUEST_SUCCEEDED","responseTime":67,"message":[],"Results":{
        "series":[{"seriesID":"CUUR0000SA0","data":[
          {"year":"2026","period":"M06","periodName":"June","latest":"true","value":"333.952","footnotes":[{}]},
          {"year":"2026","period":"M05","periodName":"May","value":"335.123","footnotes":[{"code":"R","text":"Revised"}]}
        ]}]}}
        """;

    private static BlsCpiAdapter CreateAdapter(Action<BlsOptions>? customise = null)
    {
        var bls = new BlsOptions { YearsOfHistory = 2 };
        customise?.Invoke(bls);

        return new BlsCpiAdapter(
            Options.Create(new CollectionOptions { Bls = bls }),
            NullLogger<BlsCpiAdapter>.Instance);
    }

    private static CpiObservationRecord Period(ParseResult result, string periodCode) =>
        result.Records.OfType<CpiObservationRecord>().Single(r => r.PeriodCode == periodCode);

    [Fact]
    public void BuildRequest_PostsTheOneSeriesAndTheYearRange()
    {
        var request = CreateAdapter().BuildRequest(new SourceRequestContextBuilder().Build());

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("api.bls.gov", request.Url);
        Assert.Contains("CUUR0000SA0", request.JsonBody);
        Assert.Contains("\"endyear\":\"2026\"", request.JsonBody);
        Assert.Contains("\"startyear\":\"2025\"", request.JsonBody);
    }

    [Fact]
    public void BuildRequest_NamesOnlyTheSeriesInScope()
    {
        // The series is a fact about the schema, not a row that could be edited: core.CpiObservation
        // stores nothing else, so asking for anything else could only produce rejections.
        var request = CreateAdapter().BuildRequest(new SourceRequestContextBuilder().Build());

        Assert.Contains("\"seriesid\":[\"CUUR0000SA0\"]", request.JsonBody);
    }

    [Fact]
    public void BuildRequest_OmitsRegistrationKey_WhenNoneConfigured()
    {
        // Unregistered v2 calls still work under a smaller quota, so an absent key must not
        // send an empty one — BLS rejects that outright.
        var request = CreateAdapter().BuildRequest(new SourceRequestContextBuilder().Build());

        Assert.DoesNotContain("registrationkey", request.JsonBody);
    }

    [Fact]
    public void BuildRequest_IncludesRegistrationKey_WhenConfigured()
    {
        var request = CreateAdapter(o => o.ApiKey = "test-key")
            .BuildRequest(new SourceRequestContextBuilder().Build());

        Assert.Contains("\"registrationkey\":\"test-key\"", request.JsonBody);
    }

    [Fact]
    public void Parse_MapsPeriodsToReferenceDates()
    {
        var result = CreateAdapter().Parse(SamplePayload);

        Assert.Equal(2, result.EntriesSeen);
        Assert.Empty(result.Rejections);

        var june = Period(result, "M06");
        Assert.Equal("CUUR0000SA0", june.SeriesCode);
        Assert.Equal((short)2026, june.ReferenceYear);
        Assert.Equal(new DateOnly(2026, 6, 1), june.ReferenceDate);
        Assert.Equal(PeriodType.Month, june.PeriodType);
        Assert.Equal(333.952m, june.IndexValue);
    }

    [Fact]
    public void Parse_KeepsTheAnnualAverageApartFromJanuary()
    {
        // M13 and M01 share a reference date, so only the period code tells them apart — and
        // mislabelling M13 as a month would add a phantom thirteenth reading every year.
        const string body = """
            {"status":"REQUEST_SUCCEEDED","Results":{"series":[{"seriesID":"CUUR0000SA0","data":[
              {"year":"2025","period":"M01","value":"317.671","footnotes":[{}]},
              {"year":"2025","period":"M13","value":"321.943","footnotes":[{}]},
              {"year":"2025","period":"S01","value":"320.229","footnotes":[{}]},
              {"year":"2025","period":"S02","value":"324.000","footnotes":[{}]}]}]}}
            """;

        var result = CreateAdapter().Parse(body);

        Assert.Empty(result.Rejections);
        Assert.Equal(4, result.Records.Count);

        Assert.Equal(PeriodType.Month, Period(result, "M01").PeriodType);
        Assert.Equal(PeriodType.Annual, Period(result, "M13").PeriodType);
        Assert.Equal(PeriodType.Semiannual, Period(result, "S01").PeriodType);

        Assert.Equal(Period(result, "M01").ReferenceDate, Period(result, "M13").ReferenceDate);
        Assert.Equal(new DateOnly(2025, 7, 1), Period(result, "S02").ReferenceDate);
    }

    [Fact]
    public void Parse_CapturesFootnoteCodes()
    {
        // "R" is BLS's revised marker, and the signal that a figure has moved.
        var result = CreateAdapter().Parse(SamplePayload);

        Assert.Equal("R", Period(result, "M05").Footnotes);
        Assert.Null(Period(result, "M06").Footnotes);
    }

    [Fact]
    public void Parse_RejectsAnotherSeriesInTheSamePayload()
    {
        // Defensive, like the SOFR rate-type filter: we ask for one series, and a payload
        // carrying another must not be filed against this table.
        const string body = """
            {"status":"REQUEST_SUCCEEDED","Results":{"series":[{"seriesID":"CUSR0000SA0","data":[
              {"year":"2026","period":"M06","value":"333.952","footnotes":[{}]}]}]}}
            """;

        var result = CreateAdapter().Parse(body);

        Assert.Empty(result.Records);
        Assert.Equal(RejectionReason.UnknownSeries, result.Rejections.Single().Reason);
    }

    [Fact]
    public void Parse_Throws_WhenBlsReportsFailureWithHttp200()
    {
        // The envelope, not the status code, is authoritative. Treating 200 as success here
        // would record a quota rejection as a healthy run that collected nothing.
        const string body = """
            {"status":"REQUEST_NOT_PROCESSED","message":["No API key provided"],"Results":{}}
            """;

        var ex = Assert.Throws<CollectionFailureException>(() => CreateAdapter().Parse(body));
        Assert.Equal(CollectionFailureCategory.HttpError, ex.Category);
    }

    [Fact]
    public void Parse_ReportsQuotaExhaustionAsRateLimited()
    {
        // Distinct from a generic HTTP error because the remedy is a key or a smaller budget,
        // and because it must not be retried.
        const string body = """
            {"status":"REQUEST_NOT_PROCESSED","message":["daily threshold has been reached"],"Results":{}}
            """;

        var ex = Assert.Throws<CollectionFailureException>(() => CreateAdapter().Parse(body));
        Assert.Equal(CollectionFailureCategory.RateLimited, ex.Category);
    }

    [Fact]
    public void Parse_Throws_WhenTheContractChanges()
    {
        var ex = Assert.Throws<CollectionFailureException>(() =>
            CreateAdapter().Parse("""{"status":"REQUEST_SUCCEEDED","Results":{}}"""));

        Assert.Equal(CollectionFailureCategory.SchemaChanged, ex.Category);
    }

    [Fact]
    public void Parse_Throws_OnInvalidJson()
    {
        var ex = Assert.Throws<CollectionFailureException>(() => CreateAdapter().Parse("<html>oops</html>"));

        Assert.Equal(CollectionFailureCategory.ParseError, ex.Category);
    }

    [Fact]
    public void Parse_RejectsSuppressedValues()
    {
        // BLS publishes "-" for a suppressed figure. Storing that as zero would be a fabrication.
        const string body = """
            {"status":"REQUEST_SUCCEEDED","Results":{"series":[{"seriesID":"CUUR0000SA0","data":[
              {"year":"2026","period":"M06","value":"-","footnotes":[{}]}]}]}}
            """;

        var result = CreateAdapter().Parse(body);

        Assert.Empty(result.Records);
        Assert.Equal(RejectionReason.MissingField, result.Rejections.Single().Reason);
    }

    [Fact]
    public void Parse_RejectsNonNumericValues()
    {
        const string body = """
            {"status":"REQUEST_SUCCEEDED","Results":{"series":[{"seriesID":"CUUR0000SA0","data":[
              {"year":"2026","period":"M06","value":"n/a","footnotes":[{}]}]}]}}
            """;

        var result = CreateAdapter().Parse(body);

        Assert.Equal(RejectionReason.TypeMismatch, result.Rejections.Single().Reason);
    }

    [Theory]
    [InlineData("Z99")]
    [InlineData("Q01")]
    public void Parse_RejectsUnrecognisedPeriodTokens(string period)
    {
        var body = $$$"""
            {"status":"REQUEST_SUCCEEDED","Results":{"series":[{"seriesID":"CUUR0000SA0","data":[
              {"year":"2026","period":"{{{period}}}","value":"1.0","footnotes":[{}]}]}]}}
            """;

        var result = CreateAdapter().Parse(body);

        Assert.Equal(RejectionReason.UnparseablePeriod, result.Rejections.Single().Reason);
    }

    [Fact]
    public void Parse_RejectsARepeatedPeriodWithinOnePayload()
    {
        const string body = """
            {"status":"REQUEST_SUCCEEDED","Results":{"series":[{"seriesID":"CUUR0000SA0","data":[
              {"year":"2026","period":"M06","value":"1.0","footnotes":[{}]},
              {"year":"2026","period":"M06","value":"2.0","footnotes":[{}]}]}]}}
            """;

        var result = CreateAdapter().Parse(body);

        Assert.Single(result.Records);
        Assert.Equal(RejectionReason.DuplicatePeriod, result.Rejections.Single().Reason);
    }
}
