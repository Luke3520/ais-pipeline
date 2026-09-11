namespace AisPipeline.Core.Domain;

/// <summary>
/// Column positions in the Danish Maritime Authority daily CSV, verified against
/// aisdk-2026-09-05. The header is:
///
/// # Timestamp,Type of mobile,MMSI,Latitude,Longitude,Navigational status,ROT,SOG,COG,
/// Heading,IMO,Callsign,Name,Ship type,Cargo type,Width,Length,Type of position fixing device,
/// Draught,Destination,ETA,Data source type,A,B,C,D
///
/// Indexed by position rather than by name because the header's first column is literally
/// "# Timestamp" and DMA's published column documentation does not match its own files.
/// </summary>
public static class DmaColumns
{
    public const int Count = 26;

    public const int Timestamp = 0;
    public const int TypeOfMobile = 1;
    public const int Mmsi = 2;
    public const int Latitude = 3;
    public const int Longitude = 4;
    public const int NavigationalStatus = 5;
    public const int RateOfTurn = 6;
    public const int SpeedOverGround = 7;
    public const int CourseOverGround = 8;
    public const int Heading = 9;
    public const int Imo = 10;
    public const int CallSign = 11;
    public const int Name = 12;
    public const int ShipType = 13;
    public const int CargoType = 14;
    public const int Width = 15;
    public const int Length = 16;
    public const int PositionFixingDevice = 17;
    public const int Draught = 18;
    public const int Destination = 19;
    public const int Eta = 20;
    public const int DataSourceType = 21;
    public const int SizeA = 22;
    public const int SizeB = 23;
    public const int SizeC = 24;
    public const int SizeD = 25;

    /// <summary>
    /// Timestamp format in the DMA files, UTC. Parsed with InvariantCulture: this project is
    /// developed on a Danish locale, and DMA's own documentation shows a comma decimal
    /// separator the files do not use (ADR-0008).
    /// </summary>
    public const string TimestampFormat = "dd/MM/yyyy HH:mm:ss";

    /// <summary>
    /// Values DMA writes where a field is unknown. These are strings, not blanks, and a
    /// null-or-empty check misses every one of them: IMO reads "Unknown" on 47% of feed rows.
    /// </summary>
    public static bool IsUnknown(string value) =>
        value.Length == 0
        || value.Equals("Unknown", StringComparison.Ordinal)
        || value.Equals("Unknown value", StringComparison.Ordinal)
        || value.Equals("Undefined", StringComparison.Ordinal);
}
