using AisPipeline.Core.Domain;

namespace AisPipeline.Core.Query;

/// <summary>
/// Rebuilds the domain shape from stored rows.
///
/// The read models and the domain records are deliberately separate types, so something has to map
/// between them, and where that something lives matters: this was written out by hand twice in the
/// CLI and would have been a third time in the API. One of those copies already carried a defect
/// the others did not -- it dropped the port attribution, so `reconcile` reported "AIS named no
/// port for this call" for a call whose port was sitting in the row it had been handed.
/// </summary>
public static class PortCallRebuilder
{
    public static PortCall FromStored(
        StoredPortCall call,
        IReadOnlyList<(StoredPhase Phase, StoredStop Stop)> phases) => new()
        {
            Mmsi = call.Mmsi,
            Attribution = call.PortName is null || call.PortDistanceNm is null
                ? null
                : new Geo.PortAttribution
                {
                    WpiNumber = call.PortWpiNumber ?? 0,
                    Name = call.PortName,
                    Country = call.PortCountry ?? "",
                    DistanceNm = call.PortDistanceNm.Value,
                },
            Phases = [.. phases
                .OrderBy(p => p.Phase.Sequence)
                .Select(p => new PortCallPhase(
                    p.Phase.Sequence,
                    Enum.Parse<StopPhase>(p.Phase.Phase),
                    new StopEvent
                    {
                        Mmsi = p.Stop.Mmsi,
                        StartedUtc = p.Stop.StartedUtc,
                        EndedUtc = p.Stop.EndedUtc,
                        CentroidLatitude = p.Stop.CentroidLatitude,
                        CentroidLongitude = p.Stop.CentroidLongitude,
                        MaxDriftNm = p.Stop.ObservedMaxDriftNm,
                        FixCount = p.Stop.FixCount,
                        ReliableFixCount = p.Stop.ReliableFixCount,
                        ReportedStatus = p.Stop.ReportedStatus,
                        StatusAgrees = p.Stop.StatusAgrees,
                        IsComplete = p.Stop.IsComplete,
                        DraughtFirstM = p.Stop.DraughtFirstM,
                        DraughtLastM = p.Stop.DraughtLastM,
                        FirstPositionId = p.Stop.FirstPositionId,
                        LastPositionId = p.Stop.LastPositionId,
                    }))],
        };
}
