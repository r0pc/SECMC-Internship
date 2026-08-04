using DataIntelligence.Core.Dtos;

namespace DataIntelligence.Core.Interfaces;

/// <summary>Retrieves the raw payload from the designated source, with retry and timeout.</summary>
public interface ISourceFetcher
{
    /// <summary>
    /// Fetches <paramref name="url"/>. Never throws for a network or HTTP condition — those are
    /// returned as a failed <see cref="FetchResult"/> so the run can be recorded and the
    /// scheduler can continue (FR-2). Honours <paramref name="cancellationToken"/> for shutdown.
    /// </summary>
    Task<FetchResult> FetchAsync(string url, CancellationToken cancellationToken);
}
