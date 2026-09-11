using AisPipeline.Core.Geo;

namespace AisPipeline.Tests.Geo;

public class HaversineTests
{
    [Fact]
    public void IdenticalPositionsAreZeroApartAndDoNotProduceNaN()
    {
        // Without the clamp inside Haversine, floating-point error can push the argument of
        // Asin fractionally above 1 and return NaN for a vessel that has not moved -- which is
        // most fixes of a moored ship.
        var d = Haversine.DistanceNm(55.556675, 9.762647, 55.556675, 9.762647);

        Assert.False(double.IsNaN(d));
        Assert.Equal(0.0, d, 9);
    }

    [Fact]
    public void OneMinuteOfLatitudeIsOneNauticalMile()
    {
        // The definition of the unit: 1' of latitude == 1 nm, to within the sphere approximation.
        var d = Haversine.DistanceNm(56.0, 10.0, 56.0 + (1.0 / 60.0), 10.0);

        Assert.Equal(1.0, d, 2);
    }

    [Fact]
    public void OneDegreeOfLatitudeIsSixtyNauticalMiles()
    {
        var d = Haversine.DistanceNm(56.0, 10.0, 57.0, 10.0);

        Assert.Equal(60.0, d, 1);
    }

    [Fact]
    public void DistanceIsSymmetric()
    {
        var forward = Haversine.DistanceNm(55.5, 9.7, 57.6, 10.6);
        var backward = Haversine.DistanceNm(57.6, 10.6, 55.5, 9.7);

        Assert.Equal(forward, backward, 9);
    }

    [Fact]
    public void EarthRadiusIsExpressedInNauticalMiles()
    {
        Assert.Equal(3440.065, Units.EarthRadiusNm, 3);
        Assert.Equal(1852.0, Units.MetresPerNauticalMile);
    }

    [Fact]
    public void ImpliedSpeedIsNullWhenNoTimePassed()
    {
        // 444 tanker rows in a 1.7M-row sample share a vessel-second with a different position.
        // Dividing by that elapsed time yields Infinity, which then poisons any comparison it
        // feeds (ADR-0021). Zero must be refused, not computed.
        Assert.Null(Haversine.ImpliedSpeedKn(0.5, TimeSpan.Zero));
    }

    [Fact]
    public void ImpliedSpeedIsNullWhenTimeRunsBackwards()
    {
        Assert.Null(Haversine.ImpliedSpeedKn(0.5, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void ImpliedSpeedIsDistanceOverHours()
    {
        Assert.Equal(12.0, Haversine.ImpliedSpeedKn(6.0, TimeSpan.FromMinutes(30))!.Value, 9);
    }

    [Fact]
    public void ImpliedSpeedIsDefinedAtTheSmallestPositiveInterval()
    {
        // One second is the finest resolution the feed carries, and it is the boundary on the
        // other side of the dt == 0 guard. It must compute rather than be refused.
        var speed = Haversine.ImpliedSpeedKn(0.01, TimeSpan.FromSeconds(1));

        Assert.NotNull(speed);
        Assert.Equal(36.0, speed!.Value, 6);
    }
}
