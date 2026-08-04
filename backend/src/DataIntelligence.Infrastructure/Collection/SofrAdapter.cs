using System.Globalization;
using System.Text.Json;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Exceptions;
using DataIntelligence.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace DataIntelligence.Infrastructure.Collection;

/// <summary>
/// Secured Overnight Financing Rate from the New York Fed's public markets API.
/// </summary>
/// <remarks>
/// A GET whose path encodes the lookback; no key required. One response record carries six
/// measures, and each becomes its own observation against its own series — see
/// <see cref="Measures"/>. That keeps the fact table a plain (series, date) -> value and avoids
/// five columns that would be null for every CPI row.
/// </remarks>
public sealed class SofrAdapter : ISourceAdapter
{
    private const int MaxFragmentLength = 1000;

    /// <summary>
    /// JSON field to series code. The same mapping is recorded in
    /// <c>core.Series.SourceFieldPath</c>, where it documents the lineage for anyone reading
    /// the data rather than the code.
    /// </summary>
    private static readonly (string Field, string SeriesCode)[] Measures =
    [
        ("percentRate",         "SOFR"),
        ("volumeInBillions",    "SOFR_VOL"),
        ("percentPercentile1",  "SOFR_P1"),
        ("percentPercentile25", "SOFR_P25"),
        ("percentPercentile75", "SOFR_P75"),
        ("percentPercentile99", "SOFR_P99")
    ];

    private readonly SofrOptions _options;

    public SofrAdapter(IOptions<CollectionOptions> options)
    {
        _options = options.Value.Sofr;
    }

    public string SourceCode => DataSource.NyFedSofrCode;

    public SourceRequest BuildRequest(SourceRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A rolling window rather than a single day: it costs nothing extra, and it means a
        // weekend, a holiday, or a few hours of downtime are backfilled by the next cycle
        // instead of leaving a permanent hole.
        return SourceRequest.Get(
            $"https://markets.newyorkfed.org/api/rates/secured/sofr/last/{_options.LookbackBusinessDays}.json");
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
            var seenKeys = new HashSet<(string, DateOnly)>();

            foreach (var rate in rates.EnumerateArray())
            {
                seen++;

                // The endpoint is SOFR-specific, but the payload is shared with the other
                // secured rates. Filtering defensively means a change upstream cannot quietly
                // file BGCR or TGCR values against SOFR's series.
                var type = rate.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (!string.Equals(type, "SOFR", StringComparison.OrdinalIgnoreCase))
                {
                    rejections.Add(new RejectedFragment(type, null, RejectionReason.UnknownSeries,
                        $"Record is of type '{type}', not SOFR.", Truncate(rate.ToString())));
                    continue;
                }

                var effectiveDate = rate.TryGetProperty("effectiveDate", out var d) ? d.GetString() : null;
                if (!DateOnly.TryParseExact(effectiveDate, "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var referenceDate))
                {
                    rejections.Add(new RejectedFragment("SOFR", effectiveDate,
                        RejectionReason.UnparseablePeriod,
                        $"effectiveDate '{effectiveDate}' is not an ISO date.", Truncate(rate.ToString())));
                    continue;
                }

                if (!seenKeys.Add(("SOFR", referenceDate)))
                {
                    rejections.Add(new RejectedFragment("SOFR", effectiveDate,
                        RejectionReason.DuplicatePeriod,
                        "This effective date already appeared in the same payload.",
                        Truncate(rate.ToString())));
                    continue;
                }

                // Verbatim, not interpreted: the NY Fed sets this when a published rate is
                // corrected, which is precisely what makes the row a new vintage.
                var revisionIndicator = rate.TryGetProperty("revisionIndicator", out var r)
                    ? r.GetString()
                    : null;
                var annotation = string.IsNullOrWhiteSpace(revisionIndicator) ? null : revisionIndicator;

                foreach (var (field, seriesCode) in Measures)
                {
                    if (!rate.TryGetProperty(field, out var element) || element.ValueKind == JsonValueKind.Null)
                    {
                        // Percentiles are occasionally absent on low-volume days. That is a
                        // missing measure, not a broken record, so the others still land.
                        continue;
                    }

                    if (!TryReadDecimal(element, out var value))
                    {
                        rejections.Add(new RejectedFragment(seriesCode, effectiveDate,
                            RejectionReason.TypeMismatch,
                            $"Field '{field}' is not numeric.", Truncate(rate.ToString())));
                        continue;
                    }

                    records.Add(new ObservationRecord
                    {
                        SeriesCode = seriesCode,
                        ReferenceDate = referenceDate,
                        PeriodType = PeriodType.Day,
                        Value = value,
                        SourceAnnotation = annotation
                    });
                }
            }

            return new ParseResult(records, rejections, seen);
        }
    }

    /// <summary>Accepts a JSON number or a numeric string, since the API has used both.</summary>
    private static bool TryReadDecimal(JsonElement element, out decimal value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetDecimal(out value);
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return decimal.TryParse(element.GetString(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out value);
        }

        value = 0;
        return false;
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
