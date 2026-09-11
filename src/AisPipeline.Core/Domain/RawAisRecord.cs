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

    public string? Imo { get; init; }
    public string? CallSign { get; init; }
    public string? Name { get; init; }
    public string? ShipType { get; init; }
    public double? LengthM { get; init; }
    public double? WidthM { get; init; }
}
