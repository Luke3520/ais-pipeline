namespace AisPipeline.Core.Radar;

/// <summary>A latitude/longitude box, in degrees. North and east positive.</summary>
public sealed record GeoBox(double MinLatDeg, double MaxLatDeg, double MinLonDeg, double MaxLonDeg)
{
    public double MidLatDeg => (MinLatDeg + MaxLatDeg) / 2.0;
    public double MidLonDeg => (MinLonDeg + MaxLonDeg) / 2.0;

    public bool Contains(double latDeg, double lonDeg) =>
        latDeg >= MinLatDeg && latDeg <= MaxLatDeg && lonDeg >= MinLonDeg && lonDeg <= MaxLonDeg;
}

/// <summary>
/// The waters the radar can be pointed at. Every box lies inside the clipped coastline
/// (reference/coastline/README.md: 3–16°E, 53–60°N), so no region shows land as open sea.
///
/// Chosen for what is worth watching rather than surveyed: these are viewports, not boundaries.
/// Nothing derived is computed from them.
/// </summary>
public static class RadarRegions
{
    /// <summary>The whole of the DMA feed's dense coverage.</summary>
    public static readonly GeoBox DanishWaters = new(54.3, 58.2, 7.6, 13.4);

    /// <summary>Between Jutland and Sweden. The tanker anchorages off Skagen and Frederikshavn.</summary>
    public static readonly GeoBox Kattegat = new(55.9, 57.9, 10.1, 12.9);

    /// <summary>North of Jutland, towards Norway.</summary>
    public static readonly GeoBox Skagerrak = new(57.0, 59.3, 7.6, 11.6);

    /// <summary>The Great and Little Belts, where Fredericia's tanker berths are.</summary>
    public static readonly GeoBox Belts = new(54.8, 56.0, 9.5, 11.5);

    /// <summary>Copenhagen and Malmö across the Sound.</summary>
    public static readonly GeoBox Oresund = new(55.3, 56.2, 12.2, 13.1);

    public static readonly IReadOnlyDictionary<string, GeoBox> ByName =
        new Dictionary<string, GeoBox>(StringComparer.OrdinalIgnoreCase)
        {
            ["danish-waters"] = DanishWaters,
            ["kattegat"] = Kattegat,
            ["skagerrak"] = Skagerrak,
            ["belts"] = Belts,
            ["oresund"] = Oresund,
        };
}
