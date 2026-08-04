using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Dtos;

/// <summary>Observations extracted from a payload, plus the ones that could not be.</summary>
/// <param name="Records">Observations that parsed cleanly. Still subject to validation.</param>
/// <param name="Rejections">Entries the adapter could not turn into observations.</param>
/// <param name="EntriesSeen">
/// How many data entries the payload contained, before extraction. Zero from a response that
/// fetched and parsed successfully means the publisher changed its contract — distinct from a
/// genuinely empty result set, and reported as such rather than as a silent no-op.
/// </param>
public sealed record ParseResult(
    IReadOnlyList<ObservationRecord> Records,
    IReadOnlyList<RejectedFragment> Rejections,
    int EntriesSeen);

/// <summary>An entry that could not be turned into an <see cref="ObservationRecord"/>.</summary>
/// <param name="SeriesCode">Null when the series itself could not be identified.</param>
/// <param name="ReferenceDateText">The period as published; often the reason for rejection.</param>
/// <param name="Reason">Persisted to <c>core.RejectedObservation.Reason</c>.</param>
/// <param name="Detail">Why extraction failed, specific enough to act on.</param>
/// <param name="Fragment">Truncated payload fragment for the offending entry.</param>
public sealed record RejectedFragment(
    string? SeriesCode,
    string? ReferenceDateText,
    RejectionReason Reason,
    string Detail,
    string? Fragment);
