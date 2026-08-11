using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataIntelligence.Infrastructure.Collection;

/// <inheritdoc cref="IDatasetWriter"/>
/// <remarks>
/// Keyed on the effective date, which identifies a business day on its own. A revision replaces
/// the whole day — the publisher restates the record, not one measure of it — so the hash covers
/// every measure and a corrected volume counts as a revision just as a corrected rate does.
/// </remarks>
public sealed class SofrDailyRateWriter : IDatasetWriter
{
    private readonly DataIntelligenceDbContext _db;
    private readonly ILogger<SofrDailyRateWriter> _logger;

    public SofrDailyRateWriter(DataIntelligenceDbContext db, ILogger<SofrDailyRateWriter> logger)
    {
        _db = db;
        _logger = logger;
    }

    public string SourceCode => DataSource.NyFedSofrCode;

    public async Task<DatasetWriteSummary> WriteAsync(
        CollectionRun run,
        IReadOnlyList<ObservationRecord> records,
        DateTime collectedAtPkt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(records);

        var sofrRecords = records.OfType<SofrDailyRateRecord>().ToList();

        if (sofrRecords.Count == 0)
        {
            return default;
        }

        var earliest = sofrRecords.Min(r => r.EffectiveDate);
        var latest = sofrRecords.Max(r => r.EffectiveDate);

        var currentVintages = await _db.SofrDailyRates
            .Where(r => r.IsCurrent && r.EffectiveDate >= earliest && r.EffectiveDate <= latest)
            .ToDictionaryAsync(r => r.EffectiveDate, cancellationToken);

        var inserted = 0;
        var revised = 0;
        var unchanged = 0;

        foreach (var record in sofrRecords)
        {
            var rowHash = record.ComputeRowHash();

            if (!currentVintages.TryGetValue(record.EffectiveDate, out var current))
            {
                _db.SofrDailyRates.Add(NewRate(record, run, collectedAtPkt, rowHash, 0));
                inserted++;
                continue;
            }

            if (current.RowHash.AsSpan().SequenceEqual(rowHash))
            {
                unchanged++;
                continue;
            }

            current.IsCurrent = false;
            current.SupersededAtPkt = collectedAtPkt;

            _db.SofrDailyRates.Add(NewRate(
                record, run, collectedAtPkt, rowHash, (short)(current.RevisionNumber + 1)));

            revised++;

            _logger.LogInformation(
                "SOFR {EffectiveDate:yyyy-MM-dd} revised from {Old}% to {New}%.",
                record.EffectiveDate, current.RatePercent, record.RatePercent);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return new DatasetWriteSummary(inserted, revised, unchanged);
    }

    private static SofrDailyRate NewRate(
        SofrDailyRateRecord record, CollectionRun run,
        DateTime collectedAtPkt, byte[] rowHash, short revisionNumber) =>
        new()
        {
            RateType = SofrDailyRate.RateTypeValue,
            EffectiveDate = record.EffectiveDate,
            RatePercent = record.RatePercent,
            Percentile1Percent = record.Percentile1Percent,
            Percentile25Percent = record.Percentile25Percent,
            Percentile75Percent = record.Percentile75Percent,
            Percentile99Percent = record.Percentile99Percent,
            VolumeUsdBillions = record.VolumeUsdBillions,
            RevisionIndicator = record.RevisionIndicator,
            FootnoteId = record.FootnoteId,
            RevisionNumber = revisionNumber,
            IsCurrent = true,
            CollectionRunId = run.CollectionRunId,
            CollectedAtPkt = collectedAtPkt,
            RowHash = rowHash
        };
}
