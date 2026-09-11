using System.Globalization;
using AisPipeline.Core.Domain;

namespace AisPipeline.Core.Parsing;

/// <summary>Outcome of parsing one source line: a record, or the reason it could not be read.</summary>
public readonly record struct ParseResult
{
    public RawAisRecord? Record { get; private init; }
    public string? Failure { get; private init; }

    public bool Ok => Record is not null;

    public static ParseResult Success(RawAisRecord record) => new() { Record = record };

    public static ParseResult Failed(string reason) => new() { Failure = reason };
}

/// <summary>
/// Turns an <see cref="AisSourceLine"/> into a <see cref="RawAisRecord"/>.
///
/// Pure: no I/O, no clock, no culture from the environment. Every numeric and date conversion
/// pins InvariantCulture explicitly, because this project is developed on a Danish locale where
/// the decimal separator is a comma (ADR-0008).
/// </summary>
public static class RawAisRecordParser
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static ParseResult Parse(AisSourceLine line)
    {
        if (line.Fields.Count != DmaColumns.Count)
        {
            return ParseResult.Failed(
                $"expected {DmaColumns.Count} fields, found {line.Fields.Count}");
        }

        var fields = line.Fields;

        if (!DateTime.TryParseExact(
                fields[DmaColumns.Timestamp],
                DmaColumns.TimestampFormat,
                Invariant,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var timestamp))
        {
            return ParseResult.Failed($"unparseable timestamp '{fields[DmaColumns.Timestamp]}'");
        }

        if (!long.TryParse(fields[DmaColumns.Mmsi], NumberStyles.None, Invariant, out var mmsi))
        {
            return ParseResult.Failed($"unparseable MMSI '{fields[DmaColumns.Mmsi]}'");
        }

        if (!TryDouble(fields[DmaColumns.Latitude], out var latitude))
        {
            return ParseResult.Failed($"unparseable latitude '{fields[DmaColumns.Latitude]}'");
        }

        if (!TryDouble(fields[DmaColumns.Longitude], out var longitude))
        {
            return ParseResult.Failed($"unparseable longitude '{fields[DmaColumns.Longitude]}'");
        }

        return ParseResult.Success(new RawAisRecord
        {
            SourceFile = line.SourceFile,
            SourceLine = line.LineNumber,
            RawText = line.RawText,
            TimestampUtc = timestamp,
            TypeOfMobile = fields[DmaColumns.TypeOfMobile],
            Mmsi = mmsi,
            Latitude = latitude,
            Longitude = longitude,
            NavigationalStatus = fields[DmaColumns.NavigationalStatus],
            SpeedOverGroundKn = OptionalDouble(fields[DmaColumns.SpeedOverGround]),
            CourseOverGround = OptionalDouble(fields[DmaColumns.CourseOverGround]),
            HeadingDegrees = OptionalDouble(fields[DmaColumns.Heading]),
            Imo = OptionalText(fields[DmaColumns.Imo]),
            CallSign = OptionalText(fields[DmaColumns.CallSign]),
            Name = OptionalText(fields[DmaColumns.Name]),
            ShipType = OptionalText(fields[DmaColumns.ShipType]),
            LengthM = OptionalDouble(fields[DmaColumns.Length]),
            WidthM = OptionalDouble(fields[DmaColumns.Width]),
        });
    }

    private static bool TryDouble(string value, out double parsed) =>
        double.TryParse(value, NumberStyles.Float, Invariant, out parsed);

    /// <summary>Blank or unparseable optional numerics become null, never a sentinel.</summary>
    private static double? OptionalDouble(string value) =>
        TryDouble(value, out var parsed) ? parsed : null;

    /// <summary>DMA's textual unknowns ("Unknown", "Undefined") become null.</summary>
    private static string? OptionalText(string value) =>
        DmaColumns.IsUnknown(value) ? null : value;
}
