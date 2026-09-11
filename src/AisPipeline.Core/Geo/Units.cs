namespace AisPipeline.Core.Geo;

/// <summary>
/// Unit constants. Declared once so a conversion cannot drift between call sites --
/// see docs/rules/units-and-geodesy.md.
/// </summary>
public static class Units
{
    /// <summary>One nautical mile in metres, by definition.</summary>
    public const double MetresPerNauticalMile = 1852.0;

    /// <summary>Mean Earth radius, in metres.</summary>
    public const double EarthRadiusMetres = 6_371_000.0;

    /// <summary>Mean Earth radius in nautical miles (~3440.065).</summary>
    public const double EarthRadiusNm = EarthRadiusMetres / MetresPerNauticalMile;
}
