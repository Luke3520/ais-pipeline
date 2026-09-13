using AisPipeline.Core.Quality;
using AisPipeline.Core.Query;

namespace AisPipeline.Core.Export;

/// <summary>
/// Assembles the export document from what the store already knows.
///
/// Pure, and in Core, so the shape a published site depends on is decided here and unit-tested
/// rather than emerging from whatever the CLI happened to serialise. The adapter's only job is to
/// fetch and to write bytes (ADR-0039).
/// </summary>
public static class ExportBuilder
{
    public static ExportDocument Build(
        DateTime generatedUtc,
        IReadOnlyList<StoredRun> runs,
        IReadOnlyList<QualityReportLine> rules,
        StopStatusDisagreement disagreement,
        IReadOnlyList<VesselStopStatus> stopStatus,
        IReadOnlyList<VesselFixStatus> fixStatus,
        IReadOnlyDictionary<long, StoredVessel> vessels,
        long portCalls,
        DateTime firstFixUtc,
        DateTime lastFixUtc)
    {
        var firstRunPerFile = runs
            .OrderBy(r => r.Id)
            .GroupBy(r => r.SourceFile, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(r => r.Id)
            .ToList();

        var byMmsi = new Dictionary<long, VesselRecord>();

        // Only vessels that actually lapsed reach the document. The no-lapse rows are needed for
        // the summary counts above and would otherwise add ten thousand empty records to a file a
        // browser downloads.
        foreach (var arrival in stopStatus.Where(a => a.Lapses > 0))
        {
            byMmsi[arrival.Mmsi] = WithVessel(arrival.Mmsi, vessels) with
            {
                ArrivalLapses = arrival.Lapses,
                TotalStops = arrival.TotalStops,
                HoursClaimingUnderWay = arrival.HoursClaimingUnderWay,
            };
        }

        foreach (var departure in fixStatus)
        {
            // A vessel can appear in either list or both. Merging rather than concatenating is what
            // makes "forgot in both directions" answerable at all.
            var existing = byMmsi.GetValueOrDefault(departure.Mmsi)
                ?? WithVessel(departure.Mmsi, vessels);

            byMmsi[departure.Mmsi] = existing with
            {
                DepartureLapses = departure.Lapses,
                TotalFixes = departure.TotalFixes,
                FastestKnWhileClaimingStationary = departure.FastestKn,
            };
        }

        var records = byMmsi.Values
            .OrderByDescending(v => v.ArrivalLapses + v.DepartureLapses)
            .ThenBy(v => v.Mmsi)
            .ToList();

        return new ExportDocument
        {
            Manifest = new ExportManifest
            {
                GeneratedUtc = generatedUtc,
                SourceFiles = [.. firstRunPerFile.Select(r => r.SourceFile)],
                FirstFixUtc = firstFixUtc,
                LastFixUtc = lastFixUtc,

                // One run per file. Summing every run counts a re-ingested file's rows twice, and
                // the site reads this figure as the size of the feed.
                RowsRead = firstRunPerFile.Sum(r => r.RowsRead),

                // Every run, and correct as it stands: a re-ingest inserts nothing, so this is the
                // count of distinct rows stored however many times the files were offered.
                RowsStored = runs.Sum(r => r.RowsInserted),
            },
            Summary = new ExportSummary
            {
                VesselsWithStops = stopStatus.Count,
                VesselsWithLapses = records.Count,
                Stops = disagreement.TotalStops,
                PortCalls = portCalls,
                ArrivalLapses = disagreement.Disagreeing,
                DepartureLapses = fixStatus.Sum(d => d.Lapses),
                HoursClaimingUnderWay = stopStatus.Sum(a => a.HoursClaimingUnderWay),

                // Counted over the same minimum, so the two are comparable. A vessel below it is in
                // neither population: its record is too short to be a habit either way.
                VesselsAlwaysWrong = stopStatus.Count(a =>
                    a.TotalStops >= ExportSummary.HabitMinimumStops && a.Lapses == a.TotalStops),
                VesselsAlwaysRight = stopStatus.Count(a =>
                    a.TotalStops >= ExportSummary.HabitMinimumStops && a.Lapses == 0),
            },
            Rules = rules,
            StatusDisagreement = disagreement,
            Vessels = records,
        };
    }

    private static VesselRecord WithVessel(long mmsi, IReadOnlyDictionary<long, StoredVessel> vessels)
    {
        var vessel = vessels.GetValueOrDefault(mmsi);
        return new VesselRecord { Mmsi = mmsi, Name = vessel?.Name, ShipType = vessel?.ShipType };
    }
}
