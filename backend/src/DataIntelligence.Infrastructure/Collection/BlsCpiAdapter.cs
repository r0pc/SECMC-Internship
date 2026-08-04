using System.Globalization;
using System.Text.Json;
using DataIntelligence.Core.Collection;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Exceptions;
using DataIntelligence.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataIntelligence.Infrastructure.Collection;

/// <summary>
/// Consumer Price Index from the BLS public timeseries API (v2).
/// </summary>
/// <remarks>
/// Request is a POST naming the series and year range; the API key, when configured, raises the
/// daily query budget substantially. Unregistered v2 calls still work, which is why an absent
/// key degrades rather than fails — see <see cref="BlsOptions.ApiKey"/>.
/// <para>
/// The response wraps everything in a status envelope: HTTP 200 with
/// <c>"status": "REQUEST_NOT_PROCESSED"</c> is a failure, so the status is checked before the
/// data is read. Treating 200 as success here would record an empty run as a healthy one.
/// </para>
/// </remarks>
public sealed class BlsCpiAdapter : ISourceAdapter
{
    private const int MaxFragmentLength = 1000;

    private readonly BlsOptions _options;
    private readonly ILogger<BlsCpiAdapter> _logger;

    public BlsCpiAdapter(IOptions<CollectionOptions> options, ILogger<BlsCpiAdapter> logger)
    {
        _options = options.Value.Bls;
        _logger = logger;
    }

    public string SourceCode => DataSource.BlsCpiCode;

    public SourceRequest BuildRequest(SourceRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.SeriesCodes.Count == 0)
        {
            throw new CollectionFailureException(CollectionFailureCategory.Unknown,
                "No active BLS series are configured; there is nothing to request.");
        }

        // The API caps series per request. Exceeding it fails the whole call, so the list is
        // trimmed here and the drop is logged rather than discovered as an opaque API error.
        var seriesIds = context.SeriesCodes.Take(_options.MaxSeriesPerRequest).ToArray();
        if (context.SeriesCodes.Count > seriesIds.Length)
        {
            _logger.LogWarning(
                "{Total} active BLS series exceeds the {Max}-per-request cap; requesting the first {Taken}.",
                context.SeriesCodes.Count, _options.MaxSeriesPerRequest, seriesIds.Length);
        }

        var endYear = context.UtcNow.Year;
        var startYear = endYear - Math.Max(0, _options.YearsOfHistory - 1);

        var payload = new Dictionary<string, object>
        {
            ["seriesid"] = seriesIds,
            ["startyear"] = startYear.ToString(CultureInfo.InvariantCulture),
            ["endyear"] = endYear.ToString(CultureInfo.InvariantCulture)
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            payload["registrationkey"] = _options.ApiKey;
        }

        return SourceRequest.Post(
            "https://api.bls.gov/publicAPI/v2/timeseries/data/",
            JsonSerializer.Serialize(payload));
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
                CollectionFailureCategory.ParseError, "BLS response is not valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;

            // The envelope, before the data. BLS reports quota exhaustion and bad requests here
            // with an HTTP 200, so this check is the difference between a logged failure and a
            // run that silently records zero observations as a success.
            if (root.TryGetProperty("status", out var status)
                && !string.Equals(status.GetString(), "REQUEST_SUCCEEDED", StringComparison.Ordinal))
            {
                var message = root.TryGetProperty("message", out var m) ? m.ToString() : "(no message)";
                var text = status.GetString() ?? "(no status)";

                var category = message.Contains("threshold", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("daily", StringComparison.OrdinalIgnoreCase)
                        ? CollectionFailureCategory.RateLimited
                        : CollectionFailureCategory.HttpError;

                throw new CollectionFailureException(category,
                    $"BLS returned status '{text}': {Truncate(message, 400)}");
            }

            if (!root.TryGetProperty("Results", out var results)
                || !results.TryGetProperty("series", out var seriesArray)
                || seriesArray.ValueKind != JsonValueKind.Array)
            {
                throw new CollectionFailureException(CollectionFailureCategory.SchemaChanged,
                    "BLS response has no Results.series array; the API contract has changed.");
            }

            var records = new List<ObservationRecord>();
            var rejections = new List<RejectedFragment>();
            var seen = 0;

            // Guards against the same series and period appearing twice in one payload, which
            // would otherwise surface as a confusing unique-index violation at save time.
            var seenKeys = new HashSet<(string, DateOnly, PeriodType)>();

            foreach (var series in seriesArray.EnumerateArray())
            {
                var seriesId = series.TryGetProperty("seriesID", out var sid) ? sid.GetString() : null;

                if (string.IsNullOrWhiteSpace(seriesId))
                {
                    rejections.Add(new RejectedFragment(null, null, RejectionReason.SchemaDrift,
                        "A series entry has no seriesID.", Truncate(series.ToString())));
                    continue;
                }

                if (!series.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var entry in data.EnumerateArray())
                {
                    seen++;
                    var outcome = ParseEntry(seriesId, entry);

                    if (outcome.Rejection is { } rejection)
                    {
                        rejections.Add(rejection);
                        continue;
                    }

                    var record = outcome.Record!;
                    if (!seenKeys.Add((record.SeriesCode, record.ReferenceDate, record.PeriodType)))
                    {
                        rejections.Add(new RejectedFragment(record.SeriesCode,
                            record.ReferenceDate.ToString("O"), RejectionReason.DuplicatePeriod,
                            "This series and period already appeared in the same payload.",
                            Truncate(entry.ToString())));
                        continue;
                    }

                    records.Add(record);
                }
            }

            return new ParseResult(records, rejections, seen);
        }
    }

    private static (ObservationRecord? Record, RejectedFragment? Rejection) ParseEntry(
        string seriesId, JsonElement entry)
    {
        var year = entry.TryGetProperty("year", out var y) ? y.GetString() : null;
        var period = entry.TryGetProperty("period", out var p) ? p.GetString() : null;
        var rawValue = entry.TryGetProperty("value", out var v) ? v.GetString() : null;

        if (!BlsPeriod.TryParse(year, period, out var referenceDate, out var periodType))
        {
            return (null, new RejectedFragment(seriesId, $"{year}/{period}",
                RejectionReason.UnparseablePeriod,
                $"Could not map BLS period '{period}' in year '{year}' to a reference date.",
                Truncate(entry.ToString())));
        }

        // Values arrive as strings, and BLS uses "-" for a suppressed or unavailable figure.
        if (string.IsNullOrWhiteSpace(rawValue) || rawValue == "-")
        {
            return (null, new RejectedFragment(seriesId, $"{year}/{period}",
                RejectionReason.MissingField,
                "Value is absent or suppressed by the publisher.", Truncate(entry.ToString())));
        }

        if (!decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            return (null, new RejectedFragment(seriesId, $"{year}/{period}",
                RejectionReason.TypeMismatch,
                $"Value '{Truncate(rawValue, 100)}' is not a number.", Truncate(entry.ToString())));
        }

        return (new ObservationRecord
        {
            SeriesCode = seriesId,
            ReferenceDate = referenceDate,
            PeriodType = periodType,
            SourcePeriodCode = period,
            Value = value,
            SourceAnnotation = ReadFootnotes(entry)
        }, null);
    }

    /// <summary>
    /// Collapses the footnote array into its codes. BLS emits <c>[{}]</c> when there are none,
    /// and a code such as "R" (revised) is exactly the signal that a figure has moved.
    /// </summary>
    private static string? ReadFootnotes(JsonElement entry)
    {
        if (!entry.TryGetProperty("footnotes", out var footnotes) || footnotes.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var codes = new List<string>();
        foreach (var footnote in footnotes.EnumerateArray())
        {
            if (footnote.ValueKind == JsonValueKind.Object
                && footnote.TryGetProperty("code", out var code)
                && code.GetString() is { Length: > 0 } text)
            {
                codes.Add(text);
            }
        }

        return codes.Count == 0 ? null : Truncate(string.Join(",", codes), 100);
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
