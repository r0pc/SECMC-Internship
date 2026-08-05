using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Collection;

/// <summary>
/// Gatekeeper between the adapters and the fact tables. Anything that fails here is written to
/// <c>core.RejectedObservation</c> with a reason instead of being stored or silently dropped
/// (SOW 11.1 — data validation).
/// </summary>
/// <remarks>
/// Every rule mirrors a CHECK constraint on the target table. Checking in code first is not
/// duplication for its own sake: a constraint violation aborts the whole batch with an opaque
/// SQL error, whereas a rejection here costs one logged row and lets the rest of the cycle land.
/// The constraint remains the backstop for anything that reaches the database another way.
/// </remarks>
public static class ObservationValidator
{
    /// <summary>The earliest CPI figure BLS publishes. Before this, the period token was misread.</summary>
    public const int EarliestCpiYear = 1913;

    /// <summary>
    /// The band <c>CK_Sofr_RateRange</c> enforces. Deliberately far wider than any rate the Fed
    /// has ever set: it exists to catch a decimal-shift parse bug (365 or 0.0365 where 3.65 was
    /// meant), not to have an opinion on monetary policy.
    /// </summary>
    public const decimal MinRatePercent = -5m;

    public const decimal MaxRatePercent = 25m;

    /// <summary>The widest index level <c>DECIMAL(12,3)</c> holds.</summary>
    public const decimal MaxIndexValue = 999_999_999.999m;

    /// <summary>The widest volume <c>DECIMAL(12,3)</c> holds.</summary>
    public const decimal MaxVolumeUsdBillions = 999_999_999.999m;

    /// <summary>
    /// A small tolerance on future dates: SOFR for today is published early the next business
    /// day, and a period can legitimately be dated slightly ahead of our clock in another zone.
    /// </summary>
    private const int FutureToleranceDays = 2;

    /// <summary>Validates one record. Returns null when it is fit to store.</summary>
    /// <param name="record">The parsed row.</param>
    /// <param name="utcNow">
    /// Current time, injected rather than read from the clock so the future-period rule is
    /// deterministic under test.
    /// </param>
    public static ValidationFailure? Validate(ObservationRecord record, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(record);

        return record switch
        {
            CpiObservationRecord cpi => ValidateCpi(cpi, utcNow),
            SofrDailyRateRecord sofr => ValidateSofr(sofr, utcNow),
            _ => new ValidationFailure(RejectionReason.Unknown,
                $"No validation rules are defined for {record.GetType().Name}.")
        };
    }

    private static ValidationFailure? ValidateCpi(CpiObservationRecord record, DateTime utcNow)
    {
        if (!CpiPeriod.IsKnownPeriodCode(record.PeriodCode))
        {
            return new ValidationFailure(RejectionReason.UnparseablePeriod,
                $"Period code '{record.PeriodCode}' is not one this platform stores.");
        }

        // The token and its meaning must agree, or a filter on PeriodType silently lets an annual
        // average into a monthly trend — a wrong number that looks entirely plausible.
        if (CpiPeriod.PeriodTypeFor(record.PeriodCode) != record.PeriodType)
        {
            return new ValidationFailure(RejectionReason.SchemaDrift,
                $"Period code '{record.PeriodCode}' is a "
                + $"{CpiPeriod.PeriodTypeFor(record.PeriodCode)} period, not {record.PeriodType}.");
        }

        if (record.ReferenceDate != CpiPeriod.ReferenceDateFor(record.ReferenceYear, record.PeriodCode))
        {
            return new ValidationFailure(RejectionReason.UnparseablePeriod,
                $"ReferenceDate {record.ReferenceDate:O} does not match "
                + $"{record.ReferenceYear}/{record.PeriodCode}.");
        }

        if (record.ReferenceYear < EarliestCpiYear)
        {
            return new ValidationFailure(RejectionReason.OutOfRange,
                $"Year {record.ReferenceYear} predates the earliest published CPI figure "
                + $"({EarliestCpiYear}).");
        }

        if (IsBeyondHorizon(record.ReferenceDate, utcNow))
        {
            return new ValidationFailure(RejectionReason.OutOfRange,
                $"ReferenceDate {record.ReferenceDate:O} is in the future relative to {utcNow:O}.");
        }

        // An index level is a ratio against a base period; zero or negative is not a low reading,
        // it is a misparse.
        if (record.IndexValue <= 0m || record.IndexValue > MaxIndexValue)
        {
            return new ValidationFailure(RejectionReason.OutOfRange,
                $"Index value {record.IndexValue} is outside the storable range "
                + $"(0, {MaxIndexValue}].");
        }

        return null;
    }

    private static ValidationFailure? ValidateSofr(SofrDailyRateRecord record, DateTime utcNow)
    {
        if (record.EffectiveDate == default)
        {
            return new ValidationFailure(RejectionReason.UnparseablePeriod,
                "EffectiveDate was not set by the adapter.");
        }

        if (IsBeyondHorizon(record.EffectiveDate, utcNow))
        {
            return new ValidationFailure(RejectionReason.OutOfRange,
                $"EffectiveDate {record.EffectiveDate:O} is in the future relative to {utcNow:O}.");
        }

        if (record.RatePercent < MinRatePercent || record.RatePercent > MaxRatePercent)
        {
            return new ValidationFailure(RejectionReason.OutOfRange,
                $"Rate {record.RatePercent}% is outside [{MinRatePercent}, {MaxRatePercent}]; "
                + "this usually means the value was parsed with the decimal point in the wrong place.");
        }

        if (record.VolumeUsdBillions is { } volume
            && (volume < 0m || volume > MaxVolumeUsdBillions))
        {
            return new ValidationFailure(RejectionReason.OutOfRange,
                $"Volume {volume} is outside [0, {MaxVolumeUsdBillions}] billions.");
        }

        // Percentiles are ordered by definition. Out of order means the columns were mapped to
        // the wrong fields, which no amount of downstream care would recover from.
        var ordered = new[]
        {
            record.Percentile1Percent,
            record.Percentile25Percent,
            record.Percentile75Percent,
            record.Percentile99Percent
        };

        for (var i = 1; i < ordered.Length; i++)
        {
            if (ordered[i - 1] is { } lower && ordered[i] is { } upper && lower > upper)
            {
                return new ValidationFailure(RejectionReason.SchemaDrift,
                    $"Percentiles are out of order ({lower} then {upper}); the fields appear to "
                    + "have been mapped to the wrong columns.");
            }
        }

        if (record.RevisionIndicator is { } indicator
            && indicator is not ("Y" or "N"))
        {
            return new ValidationFailure(RejectionReason.TypeMismatch,
                $"Revision indicator '{indicator}' is neither 'Y' nor 'N'.");
        }

        return null;
    }

    private static bool IsBeyondHorizon(DateOnly referenceDate, DateTime utcNow) =>
        referenceDate > DateOnly.FromDateTime(utcNow.AddDays(FutureToleranceDays));
}

/// <param name="Reason">Persisted to <c>core.RejectedObservation.Reason</c>.</param>
/// <param name="Detail">Why the row was rejected, specific enough to act on.</param>
public sealed record ValidationFailure(RejectionReason Reason, string Detail);
