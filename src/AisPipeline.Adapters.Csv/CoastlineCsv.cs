using System.Globalization;
using Sylvan.Data.Csv;

namespace AisPipeline.Adapters.Csv;

/// <summary>
/// Loads the committed coastline the radar draws (reference/coastline/README.md).
///
/// Read whole, like the gazetteer: 3,186 vertices, read once per `ais radar`. Consecutive rows that
/// share a <c>line</c> id are one polyline.
/// </summary>
public static class CoastlineCsv
{
    /// <summary>Where the committed extract lives, relative to the repository root.</summary>
    public static string DefaultPath { get; } = Path.Combine("reference", "coastline", "danish-waters.csv");

    public static IReadOnlyList<IReadOnlyList<(double LatDeg, double LonDeg)>> Load(string path)
    {
        using var reader = CsvDataReader.Create(path, DmaCsvSource.ReaderOptions());
        var lines = new List<IReadOnlyList<(double, double)>>();
        var current = new List<(double, double)>();
        string? currentId = null;
        var row = 1;

        while (reader.Read())
        {
            row++;
            var id = reader.GetString(reader.GetOrdinal("line")).Trim();
            if (id != currentId && current.Count > 0)
            {
                lines.Add(current);
                current = [];
            }

            currentId = id;

            // Refuses rather than skips. Scenery or not, a vertex that would not parse dropped
            // silently is a coastline with a hole nobody can explain.
            current.Add((Degrees(reader, "lat", path, row), Degrees(reader, "lon", path, row)));
        }

        if (current.Count > 0)
        {
            lines.Add(current);
        }

        return lines.Count > 0
            ? lines
            : throw new InvalidDataException($"{Path.GetFileName(path)} contains no coastline");
    }

    /// <summary>InvariantCulture, for the reason the gazetteer gives: this machine is Danish.</summary>
    private static double Degrees(CsvDataReader reader, string column, string path, int row)
    {
        var raw = reader.GetString(reader.GetOrdinal(column)).Trim();
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidDataException(
                $"{Path.GetFileName(path)} line {row}: {column} '{raw}' is not a number");
    }
}
