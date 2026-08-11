using DataIntelligence.Core.Dtos;
using DataIntelligence.Core.Entities;
using DataIntelligence.Core.Interfaces;
using DataIntelligence.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DataIntelligence.Infrastructure.Collection;

/// <inheritdoc cref="IDatasetWriter"/>
/// <remarks>
/// Keyed on (year, period code) rather than on the reference date, because M13 and S01 are both
/// dated 1 January: the date alone does not identify a period, and treating it as though it did
/// is how an annual average would silently overwrite a January.
/// </remarks>
public sealed class CpiObservationWriter : IDatasetWriter
{
    private readonly DataIntelligenceDbContext _db;
    private readonly ILogger<CpiObservationWriter> _logger;

    public CpiObservationWriter(DataIntelligenceDbContext db, ILogger<CpiObservationWriter> logger)
    {
        _db = db;
        _logger = logger;
    }

    public string SourceCode => DataSource.BlsCpiCode;

    public async Task<DatasetWriteSummary> WriteAsync(
        CollectionRun run,
        IReadOnlyList<ObservationRecord> records,
        DateTime collectedAtPkt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(records);

        var cpiRecords = records.OfType<CpiObservationRecord>().ToList();

        if (cpiRecords.Count == 0)
        {
            return default;
        }

        var years = cpiRecords.Select(r => r.ReferenceYear).Distinct().ToList();

        // One query for every current vintage in range, rather than one per record. Over the
        // full published history the difference between this and an N+1 is the whole cycle.
        var currentVintages = await _db.CpiObservations
            .Where(o => o.IsCurrent && years.Contains(o.ReferenceYear))
            .ToDictionaryAsync(o => (o.ReferenceYear, o.PeriodCode), cancellationToken);

        var inserted = 0;
        var revised = 0;
        var unchanged = 0;

        foreach (var record in cpiRecords)
        {
            var rowHash = record.ComputeRowHash();

            if (!currentVintages.TryGetValue((record.ReferenceYear, record.PeriodCode), out var current))
            {
                _db.CpiObservations.Add(NewObservation(record, run, collectedAtPkt, rowHash, 0));
                inserted++;
                continue;
            }

            // BLS reissued the same figure. Nothing to record: the run itself is the evidence
            // that we checked, and a second identical vintage is not merely wasteful —
            // UQ_CpiObservation_Current forbids it outright (FR-3).
            if (current.RowHash.AsSpan().SequenceEqual(rowHash))
            {
                unchanged++;
                continue;
            }

            // A genuine revision, in the order the unique index requires: release the current
            // flag before claiming it, or the insert collides with the row it replaces.
            current.IsCurrent = false;
            current.SupersededAtPkt = collectedAtPkt;

            _db.CpiObservations.Add(NewObservation(
                record, run, collectedAtPkt, rowHash, (short)(current.RevisionNumber + 1)));

            revised++;

            _logger.LogInformation(
                "CPI {Year}/{PeriodCode} revised from {Old} to {New}.",
                record.ReferenceYear, record.PeriodCode, current.IndexValue, record.IndexValue);
        }

        await _db.SaveChangesAsync(cancellationToken);

        return new DatasetWriteSummary(inserted, revised, unchanged);
    }

    private static CpiObservation NewObservation(
        CpiObservationRecord record, CollectionRun run,
        DateTime collectedAtPkt, byte[] rowHash, short revisionNumber) =>
        new()
        {
            SeriesCode = CpiObservation.SeriesCodeValue,
            ReferenceDate = record.ReferenceDate,
            ReferenceYear = record.ReferenceYear,
            PeriodCode = record.PeriodCode,
            PeriodType = record.PeriodType,
            IndexValue = record.IndexValue,
            Footnotes = record.Footnotes,
            RevisionNumber = revisionNumber,
            IsCurrent = true,
            CollectionRunId = run.CollectionRunId,
            CollectedAtPkt = collectedAtPkt,
            RowHash = rowHash
        };
}
