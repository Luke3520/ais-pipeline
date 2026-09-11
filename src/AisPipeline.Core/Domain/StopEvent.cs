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
