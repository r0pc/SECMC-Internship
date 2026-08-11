using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;

namespace DataIntelligence.Core.Interfaces;

/// <summary>
/// Writes one dataset's validated rows to its own table, applying deduplication (FR-3) and the
/// revision rule (FR-4). One implementation per dataset, selected by <see cref="SourceCode"/>.
/// </summary>
/// <remarks>
/// Split from <see cref="ICollectionRunner"/> because the run lifecycle — fetch, hash, store the
/// payload, record the outcome — is identical for both publishers, while persistence no longer
/// is: each dataset has its own table, its own natural key, and its own idea of what "the same
/// figure reissued" means. Keeping the shared half shared and the different half separate is the
/// whole point of the two-table schema.
/// </remarks>
public interface IDatasetWriter
{
    /// <summary>Matches <c>collect.DataSource.Code</c>, e.g. <c>BLS_CPI</c>.</summary>
    string SourceCode { get; }

    /// <summary>
    /// Persists the accepted records and returns what happened to them. Does not save the run
    /// itself; the caller owns the run's lifecycle.
    /// </summary>
    /// <param name="run">The run to attribute the rows to.</param>
    /// <param name="records">Records that passed validation. Never empty.</param>
    /// <param name="collectedAtPkt">
    /// One timestamp for the whole batch (FR-6), so every row from a cycle agrees about when the
    /// platform learned it.
    /// </param>
    Task<DatasetWriteSummary> WriteAsync(
        CollectionRun run,
        IReadOnlyList<ObservationRecord> records,
        DateTime collectedAtPkt,
        CancellationToken cancellationToken);
}

/// <summary>
/// What a write did. The three outcomes are counted apart because they mean different things: an
/// insert is new data, a revision means a published figure moved, and unchanged is the ordinary
/// result of polling a monthly series every hour.
/// </summary>
public readonly record struct DatasetWriteSummary(int Inserted, int Revised, int Unchanged);
