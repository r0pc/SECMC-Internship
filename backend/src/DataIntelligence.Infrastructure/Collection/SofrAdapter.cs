using System.Globalization;
using System.Text.Json;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Exceptions;
using DataIntelligence.Core.Interfaces;

namespace DataIntelligence.Infrastructure.Collection;

/// <summary>
/// Secured Overnight Financing Rate from the New York Fed's public markets API.
/// </summary>
/// <remarks>
/// A GET against the search endpoint, whose query string carries the date range; no key required.
/// The range is the current calendar year — 1 January to today — which is the annual extract the
/// schema is written against. Asking for the whole year every cycle costs one request and means a
/// gap left by an outage, a holiday, or a late revision is repaired by the next cycle rather than
/// persisting.
/// <para>
/// One response record becomes one <see cref="SofrDailyRateRecord"/>: a business day's six
/// measures are columns of one row, not six rows.
/// </para>
/// </remarks>
public sealed class SofrAdapter : ISourceAdapter
{
    private const int MaxFragmentLength = 1000;

    public string SourceCode => DataSource.NyFedSofrCode;

    public SourceRequest BuildRequest(SourceRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var today = DateOnly.FromDateTime(context.UtcNow);

        // The scheduled cycle asks for the current calendar year; a caller supplying a window
        // gets exactly what it asked for. The endpoint takes an arbitrary range, so unlike BLS
        // there is nothing to chunk.
        var window = context.Window
            ?? new CollectionWindow(new DateOnly(today.Year, 1, 1), today);

        return SourceRequest.Get(
            "https://markets.newyorkfed.org/api/rates/secured/sofr/search.json"
            + $"?startDate={Iso(window.From)}&endDate={Iso(window.To)}");
    }

    public ParseResult Parse(string content)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new CollectionFailureException(
                CollectionFailureCategory.ParseError, "SOFR response is not valid JSON.", ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("refRates", out var rates)
                || rates.ValueKind != JsonValueKind.Array)
            {
                throw new CollectionFailureException(CollectionFailureCategory.SchemaChanged,
                    "SOFR response has no refRates array; the API contract has changed.");
            }

            var records = new List<ObservationRecord>();
            var rejections = new List<RejectedFragment>();
            var seen = 0;
            var seenDates = new HashSet<DateOnly>();

            foreach (var rate in rates.EnumerateArray())
            {
                seen++;

                // Defensive rather than routine. This endpoint returns SOFR alone — verified
                // against the live API, which sends nothing else — so in normal operation this
                // rejects nothing. It matters because the same payload shape carries EFFR, OBFR,
                // TGCR and BGCR elsewhere: the CSV download has all five, and the unsecured and
                // secured rate endpoints differ only in their path. If this URL is ever changed
                // or broadened, another rate must not be filed against SOFR's table.
                var type = rate.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (!string.Equals(type, SofrDailyRate.RateTypeValue, StringComparison.OrdinalIgnoreCase))
                {
                    rejections.Add(new RejectedFragment(type, null, RejectionReason.UnknownSeries,
                        $"Record is of type '{type}', not {SofrDailyRate.RateTypeValue}.",
                        Truncate(rate.ToString())));
                    continue;
                }

                var effectiveDate = rate.TryGetProperty("effectiveDate", out var d) ? d.GetString() : null;
                if (!DateOnly.TryParseExact(effectiveDate, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var referenceDate))
                {
                    rejections.Add(new RejectedFragment(SofrDailyRate.RateTypeValue, effectiveDate,
                        RejectionReason.UnparseablePeriod,
                        $"effectiveDate '{effectiveDate}' is not an ISO date.", Truncate(rate.ToString())));
                    continue;
                }

                if (!seenDates.Add(referenceDate))
                {
                    rejections.Add(new RejectedFragment(SofrDailyRate.RateTypeValue, effectiveDate,
                        RejectionReason.DuplicatePeriod,
                        "This effective date already appeared in the same payload.",
                        Truncate(rate.ToString())));
                    continue;
                }

                // The rate itself is the one measure a record cannot do without: everything else
                // on the row describes it.
                if (!TryReadDecimal(rate, "percentRate", out var ratePercent))
                {
                    rejections.Add(new RejectedFragment(SofrDailyRate.RateTypeValue, effectiveDate,
                        RejectionReason.MissingField,
                        "percentRate is absent or not numeric.", Truncate(rate.ToString())));
                    continue;
                }

                records.Add(new SofrDailyRateRecord
                {
                    EffectiveDate = referenceDate,
                    RatePercent = ratePercent,

                    // Percentiles and volume are occasionally absent on low-volume days. That is
                    // a missing measure, not a broken record, so the day still lands.
                    Percentile1Percent = ReadOptionalDecimal(rate, "percentPercentile1"),
                    Percentile25Percent = ReadOptionalDecimal(rate, "percentPercentile25"),
                    Percentile75Percent = ReadOptionalDecimal(rate, "percentPercentile75"),
                    Percentile99Percent = ReadOptionalDecimal(rate, "percentPercentile99"),
                    VolumeUsdBillions = ReadOptionalDecimal(rate, "volumeInBillions"),

                    // Verbatim, not interpreted: the NY Fed sets this when a published rate is
                    // corrected, which is precisely what makes the row a new vintage.
                    RevisionIndicator = ReadOptionalString(rate, "revisionIndicator", 1),

                    // The CSV download has a Footnote ID column; the JSON API does not send one —
                    // confirmed against a stored live payload, whose records carry exactly
                    // effectiveDate, type, percentRate, the four percentiles, volumeInBillions and
                    // revisionIndicator. Read anyway so a CSV import or a later API field lands
                    // without a code change; null from this endpoint, always.
                    FootnoteId = ReadOptionalString(rate, "footnoteId", 20)
                });
            }

            return new ParseResult(records, rejections, seen);
        }
    }

    private static string Iso(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static decimal? ReadOptionalDecimal(JsonElement record, string field) =>
        TryReadDecimal(record, field, out var value) ? value : null;

    /// <summary>Accepts a JSON number or a numeric string, since the API has used both.</summary>
    private static bool TryReadDecimal(JsonElement record, string field, out decimal value)
    {
        value = 0;

        if (!record.TryGetProperty(field, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(element.GetString(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private static string? ReadOptionalString(JsonElement record, string field, int maxLength)
    {
        if (!record.TryGetProperty(field, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = element.GetString();

        return string.IsNullOrWhiteSpace(text) ? null : Truncate(text.Trim(), maxLength);
    }

    private static string Truncate(string? value, int maxLength = MaxFragmentLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
