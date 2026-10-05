namespace AisPipeline.Core.Radar;

/// <summary>
/// How the radar draws. Display choices, not findings: none of these changes a stored row, a stop
/// or a port call, and none was calibrated against anything except what is pleasant to watch.
/// </summary>
public static class RadarThresholds
{
    /// <summary>How much track a vessel drags behind it.</summary>
    public static readonly TimeSpan TrailDuration = TimeSpan.FromMinutes(60);

    /// <summary>
    /// A vessel silent for longer than this leaves the scope rather than sitting frozen on it.
    ///
    /// A blip that has not moved because the vessel is moored and one that has not moved because
    /// it sailed out of receiver range would otherwise look identical, and the second is a claim
    /// the data does not make.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>Degrees of arc behind the beam that are still drawn lit, as phosphor fades.</summary>
    public const double AfterglowDeg = 100.0;

    /// <summary>At most this many vessels are named on the scope at once, or names hide the sea.</summary>
    public const int MaxLabels = 5;

    /// <summary>Range rings drawn from the centre outwards.</summary>
    public const int RangeRings = 3;
}
