namespace AisPipeline.Core.Domain;

/// <summary>
/// A stored position read back for the annotate pass and for detection.
///
/// Carries its database id so a derived record can point at the exact rows it was computed
/// from, and its quality flags so detection can exclude fixes an earlier rule found
/// positionally unreliable (ADR-0021).
/// </summary>
public sealed record PositionFix
{
    public required long Id { get; init; }
    public required long Mmsi { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }

    /// <summary>Null when the vessel did not report a speed. Unknown, not zero.</summary>
    public double? SpeedOverGroundKn { get; init; }

    public string? NavigationalStatus { get; init; }

    /// <summary>Comma-joined rule ids that flagged this fix.</summary>
    public string QualityFlags { get; init; } = string.Empty;

    /// <summary>
    /// True when a rule found this fix's POSITION untrustworthy, so it must not contribute to
    /// a centroid or a drift maximum. One corrupt position otherwise drags the centroid and
    /// supplies the maximum, producing 2,387 nm of drift across hundreds of good fixes.
    /// </summary>
    public bool PositionUnreliable =>
        QualityFlags.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(id => id is "R7" or "R11");
}
