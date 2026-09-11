namespace AisPipeline.Core.Domain;

/// <summary>
/// A period during which a vessel was stationary: a berth, an anchorage, or drifting.
/// The raw material of a laytime calculation.
/// </summary>
public sealed record StopEvent
{
    public required long Mmsi { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required DateTime EndedUtc { get; init; }

    /// <summary>
    /// Only meaningful when <see cref="IsComplete"/>. A stop touching a coverage gap or the edge
    /// of the ingested window has an unknown true duration, and quoting this number for one of
    /// those is a wrong published figure (docs/rules/detection.md).
    /// </summary>
    public double DurationHours => (EndedUtc - StartedUtc).TotalHours;

    public required double CentroidLatitude { get; init; }
    public required double CentroidLongitude { get; init; }

    /// <summary>
    /// Furthest any reliable fix sat from the centroid. Small means held by mooring lines;
    /// large means swinging on an anchor chain. Records evidence rather than classifying.
    /// </summary>
    public required double MaxDriftNm { get; init; }

    public required int FixCount { get; init; }

    /// <summary>
    /// How many of those fixes contributed to the geometry -- the rest were flagged
    /// positionally unreliable and excluded (ADR-0021).
    /// </summary>
    public required int ReliableFixCount { get; init; }

    /// <summary>
    /// False when too few fixes survived exclusion for <see cref="MaxDriftNm"/> to mean
    /// anything, so the berth/anchorage question cannot be answered for this stop.
    ///
    /// The failure this prevents is silent and maximally wrong: with a single reliable fix the
    /// centroid IS that fix, so drift computes to exactly 0.0 and the stop classifies as a
    /// berth with the most confident possible value -- on precisely the vessels R11 flagged for
    /// drifting while claiming to be moored (ADR-0025).
    /// </summary>
    public bool GeometryTrustworthy =>
        ReliableFixCount >= MinimumReliableFixes
        && ReliableFixCount * 2 >= FixCount;

    /// <summary>Two points are the fewest from which a spread can be measured at all.</summary>
    public const int MinimumReliableFixes = 2;

    /// <summary>Most common navigational status the vessel reported during the stop.</summary>
    public string? ReportedStatus { get; init; }

    /// <summary>
    /// False when the vessel's own status contradicted its speed -- it was stationary while
    /// reporting that it was under way, or the reverse. Recorded rather than resolved: this is
    /// two sources disagreeing about the same fact (rule R10).
    /// </summary>
    public required bool StatusAgrees { get; init; }

    /// <summary>
    /// False when the stop touches a coverage gap or the edge of the ingested window, so its
    /// true start or end is unknown (ADR-0011).
    /// </summary>
    public required bool IsComplete { get; init; }

    public required long FirstPositionId { get; init; }
    public required long LastPositionId { get; init; }
}
