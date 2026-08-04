using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Collection;

/// <summary>
/// Gatekeeper between the parser and the fact table. Anything that fails here is written to
/// <c>core.RejectedRecord</c> with a reason instead of being stored or silently dropped —
/// a rejection spike is the earliest warning that the source's markup moved (SOW 9, Risk 1).
/// </summary>
/// <remarks>
/// Lives in Core with no infrastructure dependencies so the rules are unit-testable on their
/// own (SOW 11.1, "backend services, data validation").
/// </remarks>
public static class ScrapedRecordValidator
{
    /// <summary>Matches <c>UQ_Item_SourceKey</c> / <c>core.Item.SourceKey</c>.</summary>
    public const int MaxSourceKeyLength = 200;

    /// <summary>Matches <c>core.Item.Title</c>.</summary>
    public const int MaxTitleLength = 400;

    public const int MaxStatusTextLength = 100;

    /// <summary>
    /// Validates one record. Returns null when it is fit to store.
    /// </summary>
    /// <param name="record">The parsed record.</param>
    /// <param name="utcNow">
    /// Current time, injected rather than read from the clock so the future-timestamp rule is
    /// deterministic under test.
    /// </param>
    public static ValidationFailure? Validate(ScrapedRecord record, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.SourceKey))
        {
            return new ValidationFailure(RejectionReason.MissingField,
                "SourceKey is empty. Without the source's own identifier the record cannot be deduplicated.");
        }

        if (record.SourceKey.Length > MaxSourceKeyLength)
        {
            return new ValidationFailure(RejectionReason.OutOfRange,
                $"SourceKey is {record.SourceKey.Length} characters; the column holds {MaxSourceKeyLength}.");
        }

        if (string.IsNullOrWhiteSpace(record.Title))
        {
            return new ValidationFailure(RejectionReason.MissingField, "Title is empty.");
        }

        if (record.Title.Length > MaxTitleLength)
        {
            return new ValidationFailure(RejectionReason.OutOfRange,
                $"Title is {record.Title.Length} characters; the column holds {MaxTitleLength}.");
        }

        // Mirrors CK_ItemSnapshot_Quantity. Caught here so a bad record is one logged rejection
        // rather than an exception that aborts the whole batch insert.
        if (record.Quantity is < 0)
        {
            return new ValidationFailure(RejectionReason.OutOfRange,
                $"Quantity is {record.Quantity}, which the schema disallows.");
        }

        if (record.StatusText is { Length: > MaxStatusTextLength })
        {
            return new ValidationFailure(RejectionReason.OutOfRange,
                $"StatusText is {record.StatusText.Length} characters; the column holds {MaxStatusTextLength}.");
        }

        // CHAR(3): an ISO 4217 code or nothing. A longer value means the parser picked up the
        // wrong node, which is schema drift rather than a bad value.
        if (record.CurrencyCode is { Length: > 0 } currency && currency.Length != 3)
        {
            return new ValidationFailure(RejectionReason.SchemaDrift,
                $"CurrencyCode '{currency}' is not a 3-letter ISO 4217 code.");
        }

        // A small skew tolerance: the source's clock is not ours, and a source that publishes
        // "just now" can legitimately land a few minutes ahead.
        if (record.PublishedAtUtc is { } published && published > utcNow.AddMinutes(5))
        {
            return new ValidationFailure(RejectionReason.OutOfRange,
                $"PublishedAtUtc {published:O} is in the future relative to collection time {utcNow:O}.");
        }

        return null;
    }
}

/// <param name="Reason">Persisted to <c>core.RejectedRecord.Reason</c>.</param>
/// <param name="Detail">Why the record was rejected, specific enough to act on.</param>
public sealed record ValidationFailure(RejectionReason Reason, string Detail);
