namespace AisPipeline.Core.Geo;

/// <summary>
/// Great-circle distance. Returns nautical miles; speeds are knots.
/// The only sanctioned home for distance arithmetic in this codebase.
/// </summary>
public static class Haversine
{
    /// <summary>Great-circle distance between two positions, in nautical miles.</summary>
    public static double DistanceNm(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = double.DegreesToRadians(lat1);
        var phi2 = double.DegreesToRadians(lat2);
        var deltaPhi = phi2 - phi1;
        var deltaLambda = double.DegreesToRadians(lon2 - lon1);

        var sinHalfPhi = Math.Sin(deltaPhi / 2.0);
        var sinHalfLambda = Math.Sin(deltaLambda / 2.0);

        var h = (sinHalfPhi * sinHalfPhi)
            + (Math.Cos(phi1) * Math.Cos(phi2) * sinHalfLambda * sinHalfLambda);

        // Clamp guards against a value fractionally above 1 from floating-point error,
        // which would make Asin return NaN for two identical positions.
        return 2.0 * Units.EarthRadiusNm * Math.Asin(Math.Sqrt(Math.Min(1.0, h)));
    }

    /// <summary>
    /// Speed implied by covering <paramref name="distanceNm"/> in <paramref name="elapsed"/>.
    /// Returns null when no time passed: 444 tanker rows in a 1.7M-row sample share a
    /// vessel-second with a different position, so this divides by zero on real data
    /// rather than in theory (ADR-0021).
    /// </summary>
    public static double? ImpliedSpeedKn(double distanceNm, TimeSpan elapsed)
        => elapsed <= TimeSpan.Zero ? null : distanceNm / elapsed.TotalHours;
}
