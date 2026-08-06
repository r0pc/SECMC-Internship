using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;

namespace DataIntelligence.Core.Interfaces;

/// <summary>
/// Everything publisher-specific about one source: how to ask for data, and how to read the
/// answer. One implementation per source, selected by <see cref="SourceCode"/>.
/// </summary>
/// <remarks>
/// Request-building and parsing are deliberately the same object. They are two halves of one
/// contract with a publisher — the BLS body names the series whose codes then come back in the
/// response — and splitting them across two classes would let the halves drift apart.
/// </remarks>
public interface ISourceAdapter
{
    /// <summary>Matches <c>collect.DataSource.Code</c>, e.g. <c>BLS_CPI</c>.</summary>
    string SourceCode { get; }

    /// <summary>Builds the request for one collection cycle.</summary>
    SourceRequest BuildRequest(SourceRequestContext context);

    /// <summary>
    /// Extracts observations from a response body.
    /// </summary>
    /// <exception cref="Exceptions.CollectionFailureException">
    /// The body is not readable as the expected format, or the publisher reported an error.
    /// A well-formed response containing no entries is not an exception — it is a result with
    /// <see cref="ParseResult.EntriesSeen"/> of zero.
    /// </exception>
    ParseResult Parse(string content);
}

/// <summary>Inputs the adapter needs to build a request.</summary>
/// <param name="Source">The source row, for its endpoint and timeouts.</param>
/// <param name="UtcNow">Current time, injected so request windows are deterministic under test.</param>
/// <param name="Window">
/// The period to ask the publisher for. Null means the adapter's own default, which is what the
/// scheduled cycle uses: the last two years for CPI, the current calendar year for SOFR. A
/// backfill supplies one explicitly.
/// </param>
/// <remarks>
/// No longer carries a list of series to request. Each adapter serves exactly one dataset and
/// names its own series — <c>CUUR0000SA0</c>, or rate type SOFR — because that is now a fact
/// about the schema rather than a row that could be edited out from under the collector.
/// </remarks>
public sealed record SourceRequestContext(
    DataSource Source,
    DateTime UtcNow,
    CollectionWindow? Window = null);
