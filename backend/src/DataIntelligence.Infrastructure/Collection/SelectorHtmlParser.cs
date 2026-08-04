using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.XPath;
using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Enums;
using DataIntelligence.Core.Exceptions;
using DataIntelligence.Core.Interfaces;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataIntelligence.Infrastructure.Collection;

/// <summary>
/// Extracts records from HTML using the XPath profile in <see cref="ParserOptions"/>.
/// </summary>
/// <remarks>
/// The one source-specific component in the pipeline, and it is driven entirely by
/// configuration — so pointing the platform at the confirmed source, or repairing selectors
/// after the site's markup shifts, needs no code change (SOW 9, Risk 1).
/// </remarks>
public sealed class SelectorHtmlParser : ISourceParser
{
    /// <summary>Cap on stored fragments, so one broken page cannot bloat the rejection log.</summary>
    private const int MaxFragmentLength = 1000;

    /// <summary>Bounds a pathological user-supplied extraction pattern.</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly ParserOptions _options;
    private readonly ILogger<SelectorHtmlParser> _logger;

    public SelectorHtmlParser(IOptions<CollectionOptions> options, ILogger<SelectorHtmlParser> logger)
    {
        _options = options.Value.Parser;
        _logger = logger;
    }

    public ParseResult Parse(string content)
    {
        if (!_options.IsConfigured)
        {
            throw new CollectionFailureException(
                CollectionFailureCategory.ParseError,
                "No record selector is configured. Set Collection:Parser:RecordSelector once the "
                + "data source is signed off (SOW 0.1).");
        }

        var document = new HtmlDocument();
        try
        {
            document.LoadHtml(content);
        }
        catch (Exception ex)
        {
            throw new CollectionFailureException(
                CollectionFailureCategory.ParseError, "Response could not be loaded as HTML.", ex);
        }

        HtmlNodeCollection? nodes;
        try
        {
            nodes = document.DocumentNode.SelectNodes(_options.RecordSelector);
        }
        catch (XPathException ex)
        {
            throw new CollectionFailureException(
                CollectionFailureCategory.ParseError,
                $"Record selector '{_options.RecordSelector}' is not valid XPath.", ex);
        }

        // A well-formed page that matches nothing is the signature of a layout change. Reported
        // as a count rather than an exception so the runner can distinguish it from a source
        // that genuinely published an empty list.
        if (nodes is null || nodes.Count == 0)
        {
            _logger.LogWarning(
                "Record selector '{Selector}' matched no nodes in a {Length}-character document.",
                _options.RecordSelector, content.Length);
            return new ParseResult([], [], 0);
        }

        var records = new List<ScrapedRecord>(nodes.Count);
        var rejections = new List<RejectedFragment>();

        // Guards against a source that repeats a record, and against a SourceKey selector that
        // is not actually unique — which would otherwise surface as a confusing unique-index
        // violation at save time instead of a logged rejection here.
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            var outcome = ParseRecord(node);

            if (outcome.Rejection is { } rejection)
            {
                rejections.Add(rejection);
                continue;
            }

            var record = outcome.Record!;

            if (!seenKeys.Add(record.SourceKey))
            {
                rejections.Add(new RejectedFragment(
                    record.SourceKey,
                    RejectionReason.DuplicateKey,
                    "A record with this source key already appeared in the same payload.",
                    Truncate(node.OuterHtml)));
                continue;
            }

            records.Add(record);
        }

        return new ParseResult(records, rejections, nodes.Count);
    }

    private (ScrapedRecord? Record, RejectedFragment? Rejection) ParseRecord(HtmlNode node)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var extras = new Dictionary<string, string>(StringComparer.Ordinal);
        var missingOptional = 0;

        foreach (var (fieldName, selector) in _options.Fields)
        {
            string? raw;
            try
            {
                raw = ExtractRaw(node, selector);
            }
            catch (XPathException ex)
            {
                return (null, new RejectedFragment(null, RejectionReason.SchemaDrift,
                    $"Selector for '{fieldName}' is not valid XPath: {ex.Message}", Truncate(node.OuterHtml)));
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                if (selector.Required)
                {
                    return (null, new RejectedFragment(
                        values.GetValueOrDefault(nameof(ScrapedRecord.SourceKey)),
                        RejectionReason.MissingField,
                        $"Required field '{fieldName}' matched nothing at '{selector.Selector}'.",
                        Truncate(node.OuterHtml)));
                }

                missingOptional++;
                continue;
            }

            if (IsKnownField(fieldName))
            {
                values[fieldName] = raw;
            }
            else
            {
                extras[fieldName] = raw;
            }
        }

        if (missingOptional > _options.MaxMissingOptionalFields)
        {
            return (null, new RejectedFragment(
                values.GetValueOrDefault(nameof(ScrapedRecord.SourceKey)),
                RejectionReason.SchemaDrift,
                $"{missingOptional} configured selectors matched nothing, over the limit of "
                + $"{_options.MaxMissingOptionalFields}. The source's markup has probably changed.",
                Truncate(node.OuterHtml)));
        }

        var sourceKey = values.GetValueOrDefault(nameof(ScrapedRecord.SourceKey));
        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            return (null, new RejectedFragment(null, RejectionReason.MissingField,
                "SourceKey could not be read; the record cannot be deduplicated without it.",
                Truncate(node.OuterHtml)));
        }

        var title = values.GetValueOrDefault(nameof(ScrapedRecord.Title));
        if (string.IsNullOrWhiteSpace(title))
        {
            return (null, new RejectedFragment(sourceKey, RejectionReason.MissingField,
                "Title could not be read.", Truncate(node.OuterHtml)));
        }

        // Typed conversions, each of which can reject the record.
        decimal? primary = null, secondary = null;
        int? quantity = null;
        DateTime? publishedAt = null;

        if (values.TryGetValue(nameof(ScrapedRecord.PrimaryValue), out var primaryRaw))
        {
            if (!TryParseDecimal(primaryRaw, Field(nameof(ScrapedRecord.PrimaryValue)), out var parsed))
            {
                return (null, TypeMismatch(sourceKey, nameof(ScrapedRecord.PrimaryValue), primaryRaw, node));
            }

            primary = parsed;
        }

        if (values.TryGetValue(nameof(ScrapedRecord.SecondaryValue), out var secondaryRaw))
        {
            if (!TryParseDecimal(secondaryRaw, Field(nameof(ScrapedRecord.SecondaryValue)), out var parsed))
            {
                return (null, TypeMismatch(sourceKey, nameof(ScrapedRecord.SecondaryValue), secondaryRaw, node));
            }

            secondary = parsed;
        }

        if (values.TryGetValue(nameof(ScrapedRecord.Quantity), out var quantityRaw))
        {
            if (!TryParseDecimal(quantityRaw, Field(nameof(ScrapedRecord.Quantity)), out var parsed)
                || parsed is null
                || parsed.Value != decimal.Truncate(parsed.Value)
                || parsed.Value is > int.MaxValue or < int.MinValue)
            {
                return (null, TypeMismatch(sourceKey, nameof(ScrapedRecord.Quantity), quantityRaw, node));
            }

            quantity = (int)parsed.Value;
        }

        if (values.TryGetValue(nameof(ScrapedRecord.PublishedAtUtc), out var publishedRaw))
        {
            if (!TryParseDate(publishedRaw, Field(nameof(ScrapedRecord.PublishedAtUtc)), out var parsed))
            {
                return (null, TypeMismatch(sourceKey, nameof(ScrapedRecord.PublishedAtUtc), publishedRaw, node));
            }

            publishedAt = parsed;
        }

        var record = new ScrapedRecord
        {
            SourceKey = sourceKey.Trim(),
            Title = title.Trim(),
            CategoryCode = values.GetValueOrDefault(nameof(ScrapedRecord.CategoryCode))?.Trim(),
            CategoryName = values.GetValueOrDefault(nameof(ScrapedRecord.CategoryName))?.Trim(),
            SourceUrl = values.GetValueOrDefault(nameof(ScrapedRecord.SourceUrl))?.Trim(),
            PrimaryValue = primary,
            SecondaryValue = secondary,
            Quantity = quantity,
            StatusText = values.GetValueOrDefault(nameof(ScrapedRecord.StatusText))?.Trim(),
            CurrencyCode = values.GetValueOrDefault(nameof(ScrapedRecord.CurrencyCode))?.Trim().ToUpperInvariant(),
            PublishedAtUtc = publishedAt,
            ExtraAttributes = extras
        };

        return (record, null);

        FieldSelector? Field(string name) => _options.Fields.GetValueOrDefault(name);
    }

    private static string? ExtractRaw(HtmlNode recordNode, FieldSelector selector)
    {
        if (string.IsNullOrWhiteSpace(selector.Selector))
        {
            return null;
        }

        var target = recordNode.SelectSingleNode(selector.Selector);
        if (target is null)
        {
            return null;
        }

        // An absent attribute and an empty one are equivalent here: both mean "nothing to read",
        // which the caller turns into a missing-field rejection if the selector is required.
        var raw = selector.Attribute is { Length: > 0 } attribute
            ? target.GetAttributeValue(attribute, string.Empty)
            : HtmlEntity.DeEntitize(target.InnerText);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Collapse the whitespace that indented markup leaves in InnerText, so a value does not
        // change its hash purely because the source reformatted its HTML.
        raw = NormaliseWhitespace(raw);

        if (selector.ExtractPattern is { Length: > 0 } pattern)
        {
            try
            {
                var match = Regex.Match(raw, pattern, RegexOptions.None, RegexTimeout);
                if (!match.Success)
                {
                    return null;
                }

                raw = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                // Invalid pattern in configuration: treat as unmatched rather than crashing the run.
                return null;
            }
        }

        return raw.Trim();
    }

    private static string NormaliseWhitespace(string value)
    {
        Span<char> buffer = value.Length <= 512 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;
        var lastWasSpace = false;

        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace && length > 0)
                {
                    buffer[length++] = ' ';
                }

                lastWasSpace = true;
                continue;
            }

            buffer[length++] = c;
            lastWasSpace = false;
        }

        while (length > 0 && buffer[length - 1] == ' ')
        {
            length--;
        }

        return new string(buffer[..length]);
    }

    private static bool TryParseDecimal(string raw, FieldSelector? selector, out decimal? value)
    {
        var cleaned = raw;

        if (selector?.StripCharacters is { Length: > 0 } strip)
        {
            Span<char> filtered = stackalloc char[cleaned.Length];
            var length = 0;
            foreach (var c in cleaned)
            {
                if (!strip.Contains(c))
                {
                    filtered[length++] = c;
                }
            }

            cleaned = new string(filtered[..length]);
        }

        cleaned = cleaned.Trim();

        // InvariantCulture throughout: the source's number format is a property of the source,
        // not of whichever machine the worker happens to run on.
        var parsed = decimal.TryParse(
            cleaned,
            NumberStyles.Number | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out var result);

        value = parsed ? result : null;
        return parsed;
    }

    private static bool TryParseDate(string raw, FieldSelector? selector, out DateTime? value)
    {
        // An explicit format is exact and unambiguous. Without one, 03/04/2026 is a coin flip
        // between March and April, so a DateFormat should be configured wherever dates matter.
        if (selector?.DateFormat is { Length: > 0 } format)
        {
            if (DateTime.TryParseExact(raw, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var exact))
            {
                value = exact;
                return true;
            }

            value = null;
            return false;
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var loose))
        {
            value = loose;
            return true;
        }

        value = null;
        return false;
    }

    private static RejectedFragment TypeMismatch(string sourceKey, string field, string raw, HtmlNode node) =>
        new(sourceKey, RejectionReason.TypeMismatch,
            $"Field '{field}' could not be converted from '{Truncate(raw, 100)}'.",
            Truncate(node.OuterHtml));

    /// <summary>
    /// Field names that map to a snapshot column; anything else becomes an extension attribute.
    /// Case-insensitive to match the configuration dictionary — otherwise a profile written with
    /// <c>"sourcekey"</c> would silently store the dedup key as an attribute and reject every record.
    /// </summary>
    private static readonly HashSet<string> KnownFields = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(ScrapedRecord.SourceKey), nameof(ScrapedRecord.Title),
        nameof(ScrapedRecord.CategoryCode), nameof(ScrapedRecord.CategoryName),
        nameof(ScrapedRecord.SourceUrl), nameof(ScrapedRecord.PrimaryValue),
        nameof(ScrapedRecord.SecondaryValue), nameof(ScrapedRecord.Quantity),
        nameof(ScrapedRecord.StatusText), nameof(ScrapedRecord.CurrencyCode),
        nameof(ScrapedRecord.PublishedAtUtc)
    };

    private static bool IsKnownField(string fieldName) => KnownFields.Contains(fieldName);

    private static string Truncate(string? value, int maxLength = MaxFragmentLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
