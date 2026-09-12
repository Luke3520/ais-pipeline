using AisPipeline.Core.Geo;

namespace AisPipeline.Tests.Geo;

/// <summary>
/// Nearest-port attribution. The interesting cases are the two the measurement in ADR-0034 turned
/// up: a real berth several nautical miles from its WPI point, and a stop far from anywhere.
/// </summary>
public class NearestPortIndexTests
{
    private static GazetteerPort Port(int wpi, string name, double lat, double lon) => new()
    {
        WpiNumber = wpi,
        Name = name,
        Country = "Denmark",
        LatitudeDeg = lat,
        LongitudeDeg = lon,
    };

    // Coordinates as the committed gazetteer carries them.
    private static readonly GazetteerPort Arhus = Port(25620, "Arhus", 56.150000, 10.216667);
    private static readonly GazetteerPort Kalundborg = Port(25720, "Kalundborg", 55.683333, 11.083333);
    private static readonly GazetteerPort Rostock = Port(29260, "Rostock", 54.100000, 12.100000);

    private static NearestPortIndex Index() => new([Arhus, Kalundborg, Rostock]);

    [Fact]
    public void The_nearest_port_wins_and_carries_its_distance()
    {
        var attribution = Index().Nearest(56.15, 10.22);

        Assert.NotNull(attribution);
        Assert.Equal("Arhus", attribution!.Port.Name);
        Assert.True(attribution.DistanceNm < 0.2, $"expected a berth-scale distance, got {attribution.DistanceNm}");
        Assert.True(attribution.PlausiblyAtPort);
    }

    [Fact]
    public void A_berth_several_miles_from_the_reference_point_is_still_at_the_port()
    {
        // Rostock's real centroid sat 3.60 nm from its WPI point and the vessel was alongside. A
        // radius tight enough to reject this rejects half the berths in the window (ADR-0034).
        var attribution = Index().Nearest(54.16, 12.13);

        Assert.NotNull(attribution);
        Assert.Equal("Rostock", attribution!.Port.Name);
        Assert.True(attribution.DistanceNm > 3.0);
        Assert.True(attribution.PlausiblyAtPort);
    }

    [Fact]
    public void An_offshore_anchorage_is_named_but_not_claimed_to_be_at_the_port()
    {
        // The mid-Kattegat case: 14.35 nm off Kalundborg. Kalundborg is genuinely the nearest port
        // and saying so is useful; saying the vessel was AT Kalundborg would be false.
        var attribution = Index().Nearest(55.81, 10.74);

        Assert.NotNull(attribution);
        Assert.Equal("Kalundborg", attribution!.Port.Name);
        Assert.False(attribution.PlausiblyAtPort);
        Assert.True(attribution.DistanceNm > PortAttributionThresholds.PlausiblyAtPortNm);
    }

    [Fact]
    public void A_stop_far_from_anywhere_gets_no_port_rather_than_the_least_wrong_one()
    {
        // Arithmetic will always produce a nearest port. Beyond TooFarToNameNm that is attribution
        // by arithmetic rather than by evidence, so nothing is named.
        Assert.Null(Index().Nearest(58.5, 6.5));
    }

    [Fact]
    public void An_empty_gazetteer_names_nothing_instead_of_throwing()
    {
        Assert.Null(new NearestPortIndex([]).Nearest(56.15, 10.22));
    }

    [Fact]
    public void The_boundary_is_inclusive_so_a_call_exactly_at_the_threshold_is_at_the_port()
    {
        var atThreshold = new PortAttribution
        {
            Port = Arhus,
            DistanceNm = PortAttributionThresholds.PlausiblyAtPortNm,
        };

        Assert.True(atThreshold.PlausiblyAtPort);
    }
}
