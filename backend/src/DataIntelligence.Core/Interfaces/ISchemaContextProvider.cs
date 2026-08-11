namespace DataIntelligence.Core.Interfaces;

/// <summary>
/// Builds the description of the queryable schema that is sent to the model with every question
/// (FR-13).
/// </summary>
/// <remarks>
/// Asynchronous and cached rather than a constant, because the column lists are read from the
/// database the query will actually run against. A hand-maintained list drifts silently the first
/// time a view gains a column, and the failure it produces — a query naming something that is not
/// there, or missing something that is — surfaces as a bad answer rather than as an error.
/// </remarks>
public interface ISchemaContextProvider
{
    Task<string> GetContextAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Just the first and last date held per dataset, as a short line or two.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="GetContextAsync"/> because the step that puts results into words
    /// needs this and nothing else around it. An empty result set is ambiguous on its own — "we
    /// never collected that" and "that period is outside the series entirely" look identical — and
    /// a reader told only that the answer is empty cannot tell which. Handing the summariser the
    /// whole schema to resolve that would spend thousands of tokens on column lists it has no use
    /// for.
    /// </remarks>
    Task<string> GetCoverageAsync(CancellationToken cancellationToken);
}
