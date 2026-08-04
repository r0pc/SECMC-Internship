namespace DataIntelligence.Core.Dtos;

/// <summary>
/// A fully-formed HTTP request for one collection cycle, built by the source adapter.
/// </summary>
/// <remarks>
/// The two publishers differ at exactly this point — BLS takes a POST with a JSON body naming
/// the series and year range, the NY Fed takes a GET whose path encodes the lookback — so the
/// difference is expressed as data and the fetcher stays generic.
/// </remarks>
/// <param name="Url">Absolute request URL.</param>
/// <param name="Method">GET or POST.</param>
/// <param name="JsonBody">Request body for POST; null for GET.</param>
public sealed record SourceRequest(string Url, HttpMethod Method, string? JsonBody = null)
{
    public static SourceRequest Get(string url) => new(url, HttpMethod.Get);

    public static SourceRequest Post(string url, string jsonBody) => new(url, HttpMethod.Post, jsonBody);
}
