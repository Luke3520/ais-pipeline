namespace AisPipeline.Core.Domain;

/// <summary>
/// One AIS row after field-level parsing, before any quality rule has judged it.
///
/// Optional numerics are nullable rather than sentinel-valued: DMA blanks unavailable values
/// instead of emitting the AIS sentinels, so "no speed reported" is null here, never 102.3.
/// Unknown static strings are normalised to null by the parser, because DMA writes them as
/// "Unknown" / "Undefined" and a null-or-empty check would miss every one.
/// </summary>
public sealed record RawAisRecord
{
    public required string SourceFile { get; init; }
    public required long SourceLine { get; init; }
    public required string RawText { get; init; }

    public required DateTime TimestampUtc { get; init; }
    public required string TypeOfMobile { get; init; }
    public required long Mmsi { get; init; }
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
    public required string NavigationalStatus { get; init; }

    public double? SpeedOverGroundKn { get; init; }
    public double? CourseOverGround { get; init; }
    public double? HeadingDegrees { get; init; }

    /// <summary>Rate of turn, degrees per minute. Measured, like position and speed.</summary>
    public double? RateOfTurnDegPerMin { get; init; }

    /// <summary>
    /// Voyage data, all of it hand-entered — the same class of field as the navigational status.
    ///
    /// Draught is read off the ship and typed in; destination and ETA are typed at the start of a
    /// voyage and go stale on their own. They sit on every position row because the DMA feed
    /// flattens AIS's static and dynamic messages onto one line, and position_report is the log of
    /// what the feed said (ADR-0040).
    /// </summary>
    public double? DraughtM { get; init; }

    public string? Destination { get; init; }

    public DateTime? EtaUtc { get; init; }

    public string? Imo { get; init; }
    public string? CallSign { get; init; }
    public string? Name { get; init; }
    public string? ShipType { get; init; }
    public string? CargoType { get; init; }

    /// <summary>GPS, Combined GPS/GLONASS, Surveyed, Internal. A property of the installation.</summary>
    public string? PositionFixingDevice { get; init; }
    public double? LengthM { get; init; }
    public double? WidthM { get; init; }
}
