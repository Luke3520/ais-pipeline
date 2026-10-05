using AisPipeline.Core.Domain;

namespace AisPipeline.Core.Query;

/// <summary>
/// One stored fix as the radar and the ship's log read it: position, what the vessel said about
/// itself, what the rules said about it, and where it came from.
///
/// A read model of its own rather than <see cref="PositionFix"/>, because that one serves
/// detection and has no reason to carry provenance. This one exists to be cited, so the run and
/// the source line are the point of it.
///
/// Init-only properties for the same reason as the other read models: Dapper binds them by name,
/// and does the type conversion constructor binding would skip.
/// </summary>
public sealed record TrackFix
{
    public long Id { get; init; }
    public long Mmsi { get; init; }
    public DateTime TimestampUtc { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }

    /// <summary>Null when the vessel did not report a speed. Unknown, not zero.</summary>
    public double? SpeedOverGroundKn { get; init; }

    /// <summary>Course over ground in degrees true, or null when not reported.</summary>
    public double? CourseOverGroundDeg { get; init; }

    public string? NavigationalStatus { get; init; }

    /// <summary>Comma-joined rule ids that flagged this fix.</summary>
    public string QualityFlags { get; init; } = string.Empty;

    public long IngestRunId { get; init; }
    public long SourceLine { get; init; }

    public Citation Citation => new(IngestRunId, SourceLine);

    public bool IsFlagged(string ruleId) =>
        QualityFlags.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(ruleId, StringComparer.Ordinal);
}
