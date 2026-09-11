namespace AisPipeline.Core.Domain;

/// <summary>
/// Latest-known static data for one vessel, accumulated across a file.
///
/// Static data rides only on message-type-5 rows, so any single position row usually carries
/// none of it. Identity is therefore built by merging across rows rather than read from one
/// (ADR-0007) -- which is why filtering by a row's own ship type drops 2.6% of a tanker's fixes.
/// </summary>
public sealed record Vessel
{
    public required long Mmsi { get; init; }
    public string? Imo { get; init; }
    public string? Name { get; init; }
    public string? CallSign { get; init; }
    public string? ShipType { get; init; }
    public double? LengthM { get; init; }
    public double? WidthM { get; init; }
    public required DateTime FirstSeenUtc { get; init; }
    public required DateTime LastSeenUtc { get; init; }

    /// <summary>
    /// Fold another sighting into this identity. Later non-null values win, so a position row
    /// carrying no static data never erases what a type-5 row established.
    /// </summary>
    public Vessel MergeWith(RawAisRecord record) => this with
    {
        Imo = record.Imo ?? Imo,
        Name = record.Name ?? Name,
        CallSign = record.CallSign ?? CallSign,
        ShipType = record.ShipType ?? ShipType,
        LengthM = record.LengthM ?? LengthM,
        WidthM = record.WidthM ?? WidthM,
        FirstSeenUtc = record.TimestampUtc < FirstSeenUtc ? record.TimestampUtc : FirstSeenUtc,
        LastSeenUtc = record.TimestampUtc > LastSeenUtc ? record.TimestampUtc : LastSeenUtc,
    };

    public static Vessel From(RawAisRecord record) => new()
    {
        Mmsi = record.Mmsi,
        Imo = record.Imo,
        Name = record.Name,
        CallSign = record.CallSign,
        ShipType = record.ShipType,
        LengthM = record.LengthM,
        WidthM = record.WidthM,
        FirstSeenUtc = record.TimestampUtc,
        LastSeenUtc = record.TimestampUtc,
    };
}
