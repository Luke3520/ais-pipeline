using AisPipeline.Core.Query;

namespace AisPipeline.Core.Archive;

/// <summary>
/// Assembles the archive document from what the store still knows, while it still knows it.
///
/// Pure, and in Core, for the same reason the export builder is (ADR-0039): the shape of a
/// document other things depend on is decided and unit-tested here, and the adapter's only job is
/// bytes. It matters more here than it does there -- an export can be regenerated from the store,
/// and an archive is written exactly once, at the moment the evidence for it is destroyed.
/// </summary>
public static class ArchiveBuilder
{
    /// <param name="portCalls">The calls that will no longer be derivable after the prune.</param>
    /// <param name="phases">Phases for those calls, with their stops joined, in any order.</param>
    public static ArchiveDocument Build(
        DateTime archivedUtc,
        DateTime cutoffUtc,
        IReadOnlyList<StoredRun> runs,
        IReadOnlyList<StoredPortCall> portCalls,
        IReadOnlyList<(StoredPhase Phase, StoredStop Stop)> phases)
    {
        var phasesByCall = phases
            .GroupBy(p => p.Phase.PortCallId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Phase.Sequence).ToList());

        return new ArchiveDocument
        {
            FormatVersion = ArchiveDocument.CurrentFormatVersion,
            ArchivedUtc = archivedUtc,
            CutoffUtc = cutoffUtc,

            // Distinct, in ingest order: a file ingested twice records two runs by design, and
            // listing both would make the archive claim it came from more of the feed than exists
            // -- the same defect the export manifest had (ADR-0039).
            SourceFiles = [.. runs.OrderBy(r => r.Id).Select(r => r.SourceFile).Distinct(StringComparer.Ordinal)],

            // Chronological, not by duration. Every other listing in this project ranks by size
            // because it is answering "which are the big ones"; an archive is answering "what
            // happened", and the order it happened in is the only one that survives re-reading.
            PortCalls = [.. portCalls.OrderBy(c => c.ArrivedUtc).ThenBy(c => c.Id).Select(call => new ArchivedPortCall
            {
                Id = call.Id,
                Mmsi = call.Mmsi,
                ArrivedUtc = call.ArrivedUtc,
                DepartedUtc = call.DepartedUtc,
                WaitingHours = call.WaitingHours,
                WorkingHours = call.WorkingHours,
                UnclassifiedHours = call.UnclassifiedHours,
                IsComplete = call.IsComplete,
                PortName = call.PortName,
                PortCountry = call.PortCountry,
                PortDistanceNm = call.PortDistanceNm,
                Phases = phasesByCall.TryGetValue(call.Id, out var forCall)
                    ? [.. forCall.Select(p => new ArchivedPhase
                    {
                        Sequence = p.Phase.Sequence,
                        Phase = p.Phase.Phase,
                        StartedUtc = p.Stop.StartedUtc,
                        EndedUtc = p.Stop.EndedUtc,
                        ObservedDurationHours = p.Stop.ObservedDurationHours,
                        IsComplete = p.Stop.IsComplete,
                        ReportedStatus = p.Stop.ReportedStatus,
                        StatusAgrees = p.Stop.StatusAgrees,
                    })]
                    : [],
            })],
        };
    }
}
