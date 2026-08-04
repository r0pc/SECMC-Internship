using DataIntelligence.Core.Dtos;

namespace DataIntelligence.Core.Interfaces;

/// <summary>
/// Turns a raw payload into records. The only source-specific component in the collection
/// pipeline — everything downstream works in terms of <see cref="ScrapedRecord"/>.
/// </summary>
public interface ISourceParser
{
    /// <summary>
    /// Extracts records from <paramref name="content"/>.
    /// </summary>
    /// <exception cref="Exceptions.CollectionFailureException">
    /// The payload could not be read as the expected document type at all. A payload that
    /// parses but yields no records is not an exception — it is a result with
    /// <see cref="ParseResult.RecordNodesMatched"/> of zero, which the runner reports as a
    /// layout change.
    /// </exception>
    ParseResult Parse(string content);
}
