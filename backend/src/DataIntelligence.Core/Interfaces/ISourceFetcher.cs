using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;

namespace DataIntelligence.Core.Interfaces;

/// <summary>Executes a source request with timeout, bounded retry and failure categorisation.</summary>
public interface ISourceFetcher
{
    /// <summary>
    /// Sends <paramref name="request"/>. Never throws for a network or HTTP condition — those
    /// are returned as a failed <see cref="FetchResult"/> so the run can be recorded and the
    /// scheduler can continue (FR-2). Only cancellation propagates.
    /// </summary>
    Task<FetchResult> FetchAsync(SourceRequest request, DataSource source, CancellationToken cancellationToken);
}
