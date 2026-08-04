using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Collection;

/// <summary>
/// Gatekeeper between the adapters and the fact table. Anything that fails here is written to
/// <c>core.RejectedObservation</c> with a reason instead of being stored or silently dropped
/// (SOW 11.1 — data validation).
/// </summary>
public static class ObservationValidator
{
    /// <summary>Matches <c>core.Series.SeriesCode</c>.</summary>
    public const int MaxSeriesCodeLength = 100;

    /// <summary>
    /// The widest value <c>DECIMAL(28,8)</c> holds. Checked in code so an out-of-range figure
    /// becomes one logged rejection rather than an exception that aborts the whole batch.
    /// </summary>
    public static readonly decimal MaxValue = 99_999_999_999_999_999_9.99999999m;

    /// <summary>Validates one record. Returns null when it is fit to store.</summary>
    /// <param name="record">The parsed observation.</param>
    /// <param name="utcNow">
    /// Current time, injected rather than read from the clock so the future-period rule is
    /// deterministic under test.
    /// </param>
    public static ValidationFailure? Validate(ObservationRecord record, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.SeriesCode))
        {
            return new ValidationFailure(RejectionReason.MissingField,
                "SeriesCode is empty; the observation cannot be attributed to a series.");
        }

        if (record.SeriesCode.Length > MaxSeriesCodeLength)
        {
            return new ValidationFailure(RejectionReason.OutOfRange,
                $"SeriesCode is {record.SeriesCode.Length} characters; the column holds {MaxSeriesCodeLength}.");
        }

        if (record.ReferenceDate == default)
        {
            return new ValidationFailure(RejectionReason.UnparseablePeriod,
                "ReferenceDate was not set by the adapter.");
        }

        // Neither publisher backfills before this; a date earlier than the series itself means
        // the period token was misread rather than that the data is genuinely historic.
        if (record.ReferenceDate.Year < 1913)
        {
            return new ValidationFailure(RejectionReason.OutOfRange,
                $"ReferenceDate {record.ReferenceDate:O} predates the earliest published data.");
        }

        // A small tolerance: SOFR for today is published early the next business day, and a
        // period can legitimately be dated slightly ahead of our clock in another timezone.
        var horizon = DateOnly.FromDateTime(utcNow.AddDays(2));
        if (record.ReferenceDate > horizon)
        {
            return new ValidationFailure(RejectionReason.OutOfRange,
                $"ReferenceDate {record.ReferenceDate:O} is in the future relative to {utcNow:O}.");
        }

        if (Math.Abs(record.Value) > MaxValue)
        {
            return new ValidationFailure(RejectionReason.OutOfRange,
                $"Value {record.Value} exceeds the storable range.");
        }

        return null;
    }
}

/// <param name="Reason">Persisted to <c>core.RejectedObservation.Reason</c>.</param>
/// <param name="Detail">Why the observation was rejected, specific enough to act on.</param>
public sealed record ValidationFailure(RejectionReason Reason, string Detail);
