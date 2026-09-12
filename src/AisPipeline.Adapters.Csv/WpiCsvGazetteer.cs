using System.Globalization;
using AisPipeline.Core.Geo;
using Sylvan.Data.Csv;

namespace AisPipeline.Adapters.Csv;

/// <summary>
/// Loads the committed World Port Index extract.
///
/// Small and read whole: 145 ports, read once per `detect`. Nothing here streams, unlike
/// <see cref="DmaCsvSource"/>, because the file is a few kilobytes and the index it feeds has to
/// hold every port anyway.
/// </summary>
public static class WpiCsvGazetteer
{
    /// <summary>Where the committed extract lives, relative to the repository root.</summary>
    public static string DefaultPath { get; } = Path.Combine("reference", "ports", "wpi-baltic-north-sea.csv");

    public static IReadOnlyList<GazetteerPort> Load(string path)
    {
        using var reader = CsvDataReader.Create(path, DmaCsvSource.ReaderOptions());
        var ports = new List<GazetteerPort>();
        var line = 1;

        while (reader.Read())
        {
            line++;

            // Refuses rather than defaults. A gazetteer row that will not parse would otherwise
            // become a port at 0,0 -- null island, which rule R4 quarantines in the AIS feed and
            // which here would sit 3,400 nm from everything and never win a comparison. Silent,
            // and wrong in a way nothing would ever surface.
            var wpi = Field(reader, "wpi_number", line);
            var name = Field(reader, "port_name", line);
            var country = Field(reader, "country", line);

            if (!int.TryParse(wpi, NumberStyles.None, CultureInfo.InvariantCulture, out var wpiNumber))
            {
                throw new InvalidDataException(
                    $"{Path.GetFileName(path)} line {line}: wpi_number '{wpi}' is not a whole number");
            }

            ports.Add(new GazetteerPort
            {
                WpiNumber = wpiNumber,
                Name = name,
                AlternateName = Blank(Optional(reader, "alternate_name")),
                UnLocode = Blank(Optional(reader, "un_locode")),
                Country = country,
                LatitudeDeg = Degrees(reader, "latitude_deg", path, line),
                LongitudeDeg = Degrees(reader, "longitude_deg", path, line),
            });
        }

        if (ports.Count == 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} contains no ports");
        }

        return ports;
    }

    private static string Field(CsvDataReader reader, string column, int line)
    {
        var value = reader.GetString(reader.GetOrdinal(column)).Trim();
        return value.Length > 0
            ? value
            : throw new InvalidDataException($"line {line}: {column} is empty");
    }

    private static string Optional(CsvDataReader reader, string column) =>
        reader.GetString(reader.GetOrdinal(column)).Trim();

    private static string? Blank(string value) => value.Length == 0 ? null : value;

    /// <summary>
    /// Degrees, parsed with InvariantCulture and an explicit style.
    ///
    /// Not optional politeness: this machine runs a Danish locale, where the decimal separator is a
    /// comma, and a bare double.Parse would read 56.150000 as fifty-six million and put Arhus in
    /// the Pacific (docs/rules/units-and-geodesy.md).
    /// </summary>
    private static double Degrees(CsvDataReader reader, string column, string path, int line)
    {
        var raw = reader.GetString(reader.GetOrdinal(column)).Trim();
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException(
                $"{Path.GetFileName(path)} line {line}: {column} '{raw}' is not a number");
    }
}
