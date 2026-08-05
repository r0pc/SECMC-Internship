using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Enums;

namespace DataIntelligence.Core.Analytics;

/// <summary>
/// Everything the platform can chart, as a fixed list.
/// </summary>
/// <remarks>
/// This replaces the <c>core.Series</c> table. With one CPI series and one rate in scope, a
/// registry of rows would only be a second place for the answer to live — and a place where a
/// row could be edited into disagreement with the collector that writes the data. The schema
/// already answers "which series do we store" through the table it stores them in; this answers
/// "which lines can a chart draw", which is a presentation question and belongs in code.
/// <para>
/// A series here is a (dataset, measure) pair. CPI has one measure, so it is one entry. A SOFR
/// day carries six measures as columns of one row, so it is six entries that all read the same
/// table — which is exactly the distinction the previous six-series-one-fact-table design blurred.
/// </para>
/// </remarks>
public static class SeriesCatalog
{
    private const string CpiUrl = "https://www.bls.gov/cpi/";
    private const string SofrUrl = "https://www.newyorkfed.org/markets/reference-rates/sofr";
    private const string RateUnit = "Percent per annum";

    /// <summary>The CPI series key, used wherever a caller must name one thing.</summary>
    public const string CpiKey = "cpi";

    /// <summary>The headline SOFR series key — the rate itself.</summary>
    public const string SofrKey = "sofr";

    private static readonly SeriesDefinition[] Definitions =
    [
        new(CpiKey, Dataset.Cpi, null,
            DataSource.BlsCpiId, DataSource.BlsCpiCode, CpiObservation.SeriesCodeValue,
            "CPI-U, all items, U.S. city average, not seasonally adjusted",
            "Index 1982-84=100", 3,
            SeriesFrequency.Monthly, SeasonalAdjustment.NotSeasonallyAdjusted, CpiUrl),

        new(SofrKey, Dataset.Sofr, SofrMeasure.Rate,
            DataSource.NyFedSofrId, DataSource.NyFedSofrCode, SofrDailyRate.RateTypeValue,
            "SOFR, overnight rate", RateUnit, 2,
            SeriesFrequency.BusinessDaily, SeasonalAdjustment.NotApplicable, SofrUrl),

        new("sofr.volume", Dataset.Sofr, SofrMeasure.Volume,
            DataSource.NyFedSofrId, DataSource.NyFedSofrCode, SofrDailyRate.RateTypeValue,
            "SOFR, transaction volume", "USD billions", 0,
            SeriesFrequency.BusinessDaily, SeasonalAdjustment.NotApplicable, SofrUrl),

        new("sofr.p1", Dataset.Sofr, SofrMeasure.Percentile1,
            DataSource.NyFedSofrId, DataSource.NyFedSofrCode, SofrDailyRate.RateTypeValue,
            "SOFR, 1st percentile", RateUnit, 2,
            SeriesFrequency.BusinessDaily, SeasonalAdjustment.NotApplicable, SofrUrl),

        new("sofr.p25", Dataset.Sofr, SofrMeasure.Percentile25,
            DataSource.NyFedSofrId, DataSource.NyFedSofrCode, SofrDailyRate.RateTypeValue,
            "SOFR, 25th percentile", RateUnit, 2,
            SeriesFrequency.BusinessDaily, SeasonalAdjustment.NotApplicable, SofrUrl),

        new("sofr.p75", Dataset.Sofr, SofrMeasure.Percentile75,
            DataSource.NyFedSofrId, DataSource.NyFedSofrCode, SofrDailyRate.RateTypeValue,
            "SOFR, 75th percentile", RateUnit, 2,
            SeriesFrequency.BusinessDaily, SeasonalAdjustment.NotApplicable, SofrUrl),

        new("sofr.p99", Dataset.Sofr, SofrMeasure.Percentile99,
            DataSource.NyFedSofrId, DataSource.NyFedSofrCode, SofrDailyRate.RateTypeValue,
            "SOFR, 99th percentile", RateUnit, 2,
            SeriesFrequency.BusinessDaily, SeasonalAdjustment.NotApplicable, SofrUrl)
    ];

    private static readonly Dictionary<string, SeriesDefinition> ByKey =
        Definitions.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>In catalogue order: CPI, then SOFR's rate and its distribution.</summary>
    public static IReadOnlyList<SeriesDefinition> All => Definitions;

    /// <summary>
    /// Looks up a series by key, case-insensitively. Returns false for anything unrecognised
    /// rather than guessing, so a stale bookmark becomes a 404 and not a wrong chart.
    /// </summary>
    public static bool TryGet(string? key, out SeriesDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            return ByKey.TryGetValue(key.Trim(), out definition!);
        }

        definition = null!;
        return false;
    }

    public static SeriesDefinition Get(string key) =>
        TryGet(key, out var definition)
            ? definition
            : throw new KeyNotFoundException($"No series is registered with key '{key}'.");
}

/// <summary>
/// One chartable measure: where it comes from, and how it must be rendered.
/// </summary>
/// <param name="Key">Stable identifier used in URLs and saved dashboard views.</param>
/// <param name="Dataset">Which table the values live in.</param>
/// <param name="Measure">Which column, for SOFR. Null for CPI, which has one.</param>
/// <param name="DataSourceId">Matches <c>collect.DataSource.DataSourceId</c>.</param>
/// <param name="SourceCode">Matches <c>collect.DataSource.Code</c>.</param>
/// <param name="PublisherCode">
/// The publisher's own identifier — the BLS series id, or the NY Fed rate type. What to quote
/// when asking the publisher about a number.
/// </param>
/// <param name="Unit">
/// Verbatim from the publisher, and not decoration. Values are stored exactly as published and
/// never rescaled, so a chart that ignores this will plot SOFR volume in billions against CPI
/// index points on one axis and look fine while being nonsense.
/// </param>
/// <param name="DecimalPlaces">As published, for display fidelity.</param>
public sealed record SeriesDefinition(
    string Key,
    Dataset Dataset,
    SofrMeasure? Measure,
    byte DataSourceId,
    string SourceCode,
    string PublisherCode,
    string Title,
    string Unit,
    byte DecimalPlaces,
    SeriesFrequency Frequency,
    SeasonalAdjustment SeasonalAdjustment,
    string SourceUrl);
