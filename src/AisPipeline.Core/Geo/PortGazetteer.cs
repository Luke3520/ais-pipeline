namespace AisPipeline.Core.Geo;

/// <summary>One port as the gazetteer knows it: a name, an id, and a single point.</summary>
public sealed record GazetteerPort
{
    /// <summary>NGA's World Port Index number. Stable, and the join key back to the published index.</summary>
    public required int WpiNumber { get; init; }

    public required string Name { get; init; }

    public string? AlternateName { get; init; }

    public string? UnLocode { get; init; }

    public required string Country { get; init; }

    public required double LatitudeDeg { get; init; }

    public required double LongitudeDeg { get; init; }
}

/// <summary>
/// The nearest port to a position, and how far away it is.
///
/// Distance is not a detail of the lookup -- it is half the answer. A World Port Index record is one
/// nominal point near the harbour entrance, and the offset from it to a berth scales with the size
/// of the port: measured against 362 detected calls, vessels genuinely alongside sit anywhere from
/// 0.11 nm (Arhus) to 3.60 nm (Rostock), while a mid-Kattegat anchorage sits 14.35 nm off
/// Kalundborg. The distribution is continuous with no gap, so no radius separates the two, and a
/// name without its distance would make an anchorage at sea indistinguishable from a berth
/// (reference/ports/README.md, ADR-0034).
/// </summary>
public sealed record PortAttribution
{
    public required GazetteerPort Port { get; init; }

    public required double DistanceNm { get; init; }

    /// <summary>
    /// Whether the vessel was plausibly at this port rather than merely nearest to it.
    ///
    /// A heuristic, and named as one. The threshold admits every berth observed in the window and
    /// excludes the offshore anchorages, but the underlying distribution has no natural boundary --
    /// so a call just either side of it is a question, not an answer.
    /// </summary>
    public bool PlausiblyAtPort => DistanceNm <= PortAttributionThresholds.PlausiblyAtPortNm;
}

/// <summary>Thresholds for attributing a position to a port. Heuristics, measured, named here once.</summary>
public static class PortAttributionThresholds
{
    /// <summary>
    /// How close a stop centroid must sit to a World Port Index point to be called that port's.
    ///
    /// 5 nm, from the seven-day window: the largest offset observed for a vessel unambiguously
    /// alongside was 3.60 nm (Rostock), and the nearest offshore anchorage that clearly is not in
    /// port was 14.35 nm (off Kalundborg). Anything from roughly 4 to 13 nm would separate those
    /// two observations; 5 nm sits just above the berths with room for a larger port than any in
    /// this dataset.
    ///
    /// This is the weakest number in the feature and the README says so. It is a property of how
    /// NGA places its reference points, not of vessels, so more AIS data will not refine it --
    /// only a dataset with actual port extents would (ADR-0034).
    /// </summary>
    public const double PlausiblyAtPortNm = 5.0;

    /// <summary>
    /// Beyond this, no port is named at all.
    ///
    /// 25 nm. The four calls in the window past 21 nm are stops in open water, and the farthest sat
    /// 57.20 nm from Egersund -- naming it "Egersund" would be attribution by arithmetic rather
    /// than by evidence. A stop this far from anywhere gets no port, which is a fact about the stop
    /// and not a gap in the gazetteer.
    /// </summary>
    public const double TooFarToNameNm = 25.0;
}

/// <summary>
/// Nearest-port lookup over a loaded gazetteer.
///
/// Pure, and takes the ports as data rather than reading a file, so it stays inside Core's no-I/O
/// rule and is testable against a three-port list instead of 145.
/// </summary>
public sealed class NearestPortIndex
{
    private readonly IReadOnlyList<GazetteerPort> _ports;

    public NearestPortIndex(IReadOnlyList<GazetteerPort> ports) => _ports = ports;

    public int Count => _ports.Count;

    /// <summary>
    /// The nearest port, or null when nothing is close enough to name.
    ///
    /// Linear. 145 ports against 362 calls is 52,000 distance calculations, which is nothing; an
    /// index would be optimising a rounding error in the runtime of `detect`.
    /// </summary>
    public PortAttribution? Nearest(double latitudeDeg, double longitudeDeg)
    {
        GazetteerPort? best = null;
        var bestNm = double.MaxValue;

        foreach (var port in _ports)
        {
            var nm = Haversine.DistanceNm(latitudeDeg, longitudeDeg, port.LatitudeDeg, port.LongitudeDeg);
            if (nm < bestNm)
            {
                bestNm = nm;
                best = port;
            }
        }

        return best is null || bestNm > PortAttributionThresholds.TooFarToNameNm
            ? null
            : new PortAttribution { Port = best, DistanceNm = bestNm };
    }
}
