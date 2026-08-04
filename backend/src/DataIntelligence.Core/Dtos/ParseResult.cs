using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Dtos;

/// <summary>Records extracted from a payload, plus the ones that could not be extracted.</summary>
/// <param name="Records">Records that parsed cleanly. Still subject to validation.</param>
/// <param name="Rejections">Fragments that matched the record selector but could not be read.</param>
/// <param name="RecordNodesMatched">
/// How many nodes the record selector matched, before any field extraction. Zero on a page
/// that fetched successfully is the signature of a layout change, not an empty result set.
/// </param>
public sealed record ParseResult(
    IReadOnlyList<ScrapedRecord> Records,
    IReadOnlyList<RejectedFragment> Rejections,
    int RecordNodesMatched);

/// <summary>A record fragment that could not be turned into a <see cref="ScrapedRecord"/>.</summary>
/// <param name="SourceKey">Null when the record's key itself could not be read.</param>
/// <param name="Reason">Persisted to <c>core.RejectedRecord.Reason</c>.</param>
/// <param name="Detail">Why extraction failed, specific enough to act on.</param>
/// <param name="Fragment">Truncated markup for the offending record.</param>
public sealed record RejectedFragment(
    string? SourceKey,
    RejectionReason Reason,
    string Detail,
    string? Fragment);
