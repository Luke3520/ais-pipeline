using AisPipeline.Core.Domain;
using AisPipeline.Core.Geo;

namespace AisPipeline.Core.Detection;

/// <summary>
/// Chains a vessel's consecutive nearby stops into port calls, and labels each stop a berth or
/// an anchorage from how far the vessel drifted.
///
/// This is what reaches waiting-versus-working time without a port boundary dataset: the split
/// comes from geometry and timestamps alone.
/// </summary>
public sealed class PortCallChainer
{
    private readonly DetectionThresholds _thresholds;

    public PortCallChainer(DetectionThresholds? thresholds = null) =>
        _thresholds = thresholds ?? DetectionThresholds.Default;

    /// <summary>
    /// A vessel held by mooring lines barely moves; one on an anchor chain swings with tide and
    /// wind. Drift is normalised by the square root of the stop's duration before comparing,
    /// because measured drift grows with duration for reasons unrelated to how the vessel is
    /// held (ADR-0024).
    ///
    /// Calibrated against the vessels' own reported status -- the best available proxy, not
    /// ground truth. This project exists because that field is unreliable, so the ceiling on
    /// this classifier is well below 100%.
    /// </summary>
    public StopPhase Classify(StopEvent stop)
    {
        // No geometry, no answer. With one surviving fix the centroid is that fix and drift is
        // exactly 0.0, which would classify as the most confident possible berth -- on the
        // vessels most likely to have been drifting (ADR-0025).
        if (!stop.GeometryTrustworthy)
        {
            return StopPhase.Unknown;
        }

        // A stop shorter than the minimum duration cannot reach here, but guard the root
        // anyway rather than risk a divide-by-zero producing Infinity and a silent Anchorage.
        var hours = Math.Max(stop.DurationHours, 1.0 / 3600.0);
        var normalised = stop.MaxDriftNm / Math.Sqrt(hours);

        return normalised < _thresholds.BerthDriftPerSqrtHour ? StopPhase.Berth : StopPhase.Anchorage;
    }

    /// <summary>
    /// Chain stops for one vessel. <paramref name="stops"/> must be that vessel's stops in
    /// ascending start order.
    /// </summary>
    public IReadOnlyList<PortCall> Chain(IReadOnlyList<StopEvent> stops)
    {
        var calls = new List<PortCall>();
        var current = new List<StopEvent>();

        foreach (var stop in stops)
        {
            if (current.Count > 0 && !BelongsWith(current[^1], stop))
            {
                calls.Add(Build(current));
                current = [];
            }

            current.Add(stop);
        }

        if (current.Count > 0)
        {
            calls.Add(Build(current));
        }

        return calls;
    }

    /// <summary>
    /// Two stops are one visit when the vessel stayed in the area and came back soon. Shifting
    /// from an anchorage to a berth in the same port is one call; steaming away and returning
    /// a week later is two.
    /// </summary>
    private bool BelongsWith(StopEvent previous, StopEvent next)
    {
        if (next.StartedUtc - previous.EndedUtc > _thresholds.PortCallMaximumGap)
        {
            return false;
        }

        var separation = Haversine.DistanceNm(
            previous.CentroidLatitude, previous.CentroidLongitude,
            next.CentroidLatitude, next.CentroidLongitude);

        return separation <= _thresholds.PortCallRadiusNm;
    }

    private PortCall Build(List<StopEvent> stops) => new()
    {
        Mmsi = stops[0].Mmsi,
        Phases = [.. stops.Select((s, i) => new PortCallPhase(i, Classify(s), s))],
    };
}
