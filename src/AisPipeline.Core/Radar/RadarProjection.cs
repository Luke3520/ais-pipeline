namespace AisPipeline.Core.Radar;

/// <summary>
/// Latitude/longitude to braille dots: equirectangular, scaled by cos(mid-latitude).
///
/// At 56°N a degree of longitude is 0.56 of a degree of latitude, so an unscaled plot stretches
/// Denmark to nearly twice its width. The cosine at the box's middle corrects that to within a
/// few percent across any region the radar names, which is all a picture needs. It is not a
/// conformal projection and nothing is measured on it.
///
/// Braille dots are close to square on a terminal: a character cell is about twice as tall as it
/// is wide, and holds two dots across and four down. So one scale serves both axes, and the box is
/// centred in whatever the terminal leaves over.
/// </summary>
public sealed class RadarProjection
{
    private readonly GeoBox _box;
    private readonly double _cosMidLat;
    private readonly double _dotsPerDegLat;
    private readonly double _offsetXDots;
    private readonly double _offsetYDots;

    public RadarProjection(GeoBox box, int widthDots, int heightDots)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(widthDots, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(heightDots, 1);

        _box = box;
        WidthDots = widthDots;
        HeightDots = heightDots;
        _cosMidLat = Math.Cos(box.MidLatDeg * Math.PI / 180.0);

        var spanXDeg = (box.MaxLonDeg - box.MinLonDeg) * _cosMidLat;
        var spanYDeg = box.MaxLatDeg - box.MinLatDeg;
        _dotsPerDegLat = Math.Min(widthDots / spanXDeg, heightDots / spanYDeg);

        _offsetXDots = (widthDots - spanXDeg * _dotsPerDegLat) / 2.0;
        _offsetYDots = (heightDots - spanYDeg * _dotsPerDegLat) / 2.0;
    }

    public int WidthDots { get; }
    public int HeightDots { get; }

    /// <summary>
    /// How many nautical miles one dot spans. A minute of latitude is one nautical mile, so a
    /// degree is 60, and the scale is the same in both axes by construction.
    /// </summary>
    public double NmPerDot => MinutesPerDegree / _dotsPerDegLat;

    private const double MinutesPerDegree = 60.0;

    /// <summary>Dot coordinates, x right and y down. May fall outside the canvas.</summary>
    public (double X, double Y) Project(double latDeg, double lonDeg) =>
        (_offsetXDots + (lonDeg - _box.MinLonDeg) * _cosMidLat * _dotsPerDegLat,
         _offsetYDots + (_box.MaxLatDeg - latDeg) * _dotsPerDegLat);

    public bool OnCanvas(double xDots, double yDots) =>
        xDots >= 0 && yDots >= 0 && xDots < WidthDots && yDots < HeightDots;

    /// <summary>The distance in nautical miles a ring of this many dots represents.</summary>
    public double DotsToNm(double dots) => dots * NmPerDot;

    /// <summary>The number of dots a distance in nautical miles spans.</summary>
    public double NmToDots(double distanceNm) => distanceNm / NmPerDot;
}
