using AisPipeline.Core.Detection;
using AisPipeline.Core.Domain;
using AisPipeline.Core.Geo;

namespace AisPipeline.Tests.Detection;

public class PortCallChainerTests
{
    private static readonly DateTime T0 = new(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);

    private static StopEvent Stop(
        double startHour,
        double hours,
        double driftNm,
        double lat = 56.0,
        double lon = 10.0) => new()
        {
            Mmsi = 219000001,
            StartedUtc = T0.AddHours(startHour),
            EndedUtc = T0.AddHours(startHour + hours),
            CentroidLatitude = lat,
            CentroidLongitude = lon,
            MaxDriftNm = driftNm,
            FixCount = 100,
            ReliableFixCount = 100,
            ReportedStatus = "Moored",
            StatusAgrees = true,
            IsComplete = true,
            FirstPositionId = 1,
            LastPositionId = 2,
        };

    /// <summary>
    /// North-south latitude offset covering exactly <paramref name="nm"/> nautical miles under
    /// the same spherical model Haversine uses.
    ///
    /// Not nm/60. "One nautical mile is one minute of latitude" holds by definition against the
    /// real Earth, but on a sphere of mean radius one minute of arc is 1.0006 nm -- 0.06% out.
    /// That is invisible everywhere except a boundary test asserting behaviour at exactly the
    /// threshold, where it decides the answer.
    /// </summary>
    private static double Nm(double nm) => nm / (Units.EarthRadiusNm * Math.PI / 180.0);

    [Theory]
    // Over a 4-hour stop, sqrt(4) = 2, so the effective cutoff is 0.008 * 2 = 0.016 nm.
    [InlineData(4, 0.0159, StopPhase.Berth)]
    [InlineData(4, 0.0160, StopPhase.Anchorage)]   // boundary: NOT below the scaled threshold
    // Over 36 hours, sqrt(36) = 6, so the same drift that was an anchorage at 4 h is a berth.
    [InlineData(36, 0.0479, StopPhase.Berth)]
    [InlineData(36, 0.0480, StopPhase.Anchorage)]
    public void NormalisedDriftDecidesBerthVersusAnchorage(
        double hours, double driftNm, StopPhase expected) =>
        Assert.Equal(expected, new PortCallChainer().Classify(Stop(0, hours, driftNm)));

    [Fact]
    public void TheSameDriftMeansDifferentThingsOverDifferentDurations()
    {
        // The defect a fixed threshold caused: one vessel at one quay alternating Berth and
        // Anchorage eleven times in a single port call, because its longer stops accumulated
        // more GPS wander than its shorter ones (ADR-0024).
        const double drift = 0.03;

        Assert.Equal(StopPhase.Anchorage, new PortCallChainer().Classify(Stop(0, 2, drift)));
        Assert.Equal(StopPhase.Berth, new PortCallChainer().Classify(Stop(0, 48, drift)));
    }

    [Fact]
    public void AMooredVesselStaysABerthHoweverLongItSits()
    {
        // Measured medians for self-reported "Moored": 0.0013 nm under 2 h rising to 0.0346 nm
        // past 72 h. All of them must classify the same way.
        var chainer = new PortCallChainer();

        Assert.Equal(StopPhase.Berth, chainer.Classify(Stop(0, 1, 0.0013)));
        Assert.Equal(StopPhase.Berth, chainer.Classify(Stop(0, 9, 0.0212)));
        Assert.Equal(StopPhase.Berth, chainer.Classify(Stop(0, 100, 0.0346)));
    }

    [Fact]
    public void AnAnchoredVesselStaysAnAnchorageHoweverLongItSits()
    {
        // Measured medians for self-reported "At anchor" across the same duration bands.
        var chainer = new PortCallChainer();

        Assert.Equal(StopPhase.Anchorage, chainer.Classify(Stop(0, 1, 0.0197)));
        Assert.Equal(StopPhase.Anchorage, chainer.Classify(Stop(0, 9, 0.0810)));
        Assert.Equal(StopPhase.Anchorage, chainer.Classify(Stop(0, 100, 0.1414)));
    }

    [Fact]
    public void AnchorageThenBerthNearbyIsOnePortCall()
    {
        // The shape that matters commercially: wait on the hook, shift to the quay, work cargo.
        var calls = new PortCallChainer().Chain([
            Stop(0, 19.3, driftNm: 0.09, lat: 57.64, lon: 10.64),
            Stop(21, 31.8, driftNm: 0.002, lat: 57.64 + Nm(6.0), lon: 10.64),
        ]);

        var call = Assert.Single(calls);
        Assert.Equal(2, call.Phases.Count);
        Assert.Equal(StopPhase.Anchorage, call.Phases[0].Phase);
        Assert.Equal(StopPhase.Berth, call.Phases[1].Phase);
        Assert.Equal(19.3, call.WaitingHours, 1);
        Assert.Equal(31.8, call.WorkingHours, 1);
    }

    [Fact]
    public void StopsFarApartAreSeparatePortCalls()
    {
        var calls = new PortCallChainer().Chain([
            Stop(0, 5, 0.002, lat: 56.0),
            Stop(10, 5, 0.002, lat: 56.0 + Nm(50.0)),
        ]);

        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void StopsLongApartInTimeAreSeparatePortCallsEvenAtTheSameBerth()
    {
        // Same quay, a week apart, is two visits -- not one call with a week of "working".
        var calls = new PortCallChainer().Chain([
            Stop(0, 5, 0.002),
            Stop(24 * 7, 5, 0.002),
        ]);

        Assert.Equal(2, calls.Count);
    }

    [Theory]
    [InlineData(11.9, 1)]   // within the gap: one call
    [InlineData(12.0, 1)]   // boundary: exactly 12 hours still chains
    [InlineData(12.1, 2)]   // first value beyond: a separate call
    public void PortCallGapBoundary(double gapHours, int expectedCalls)
    {
        var calls = new PortCallChainer().Chain([
            Stop(0, 5, 0.002),
            Stop(5 + gapHours, 5, 0.002),
        ]);

        Assert.Equal(expectedCalls, calls.Count);
    }

    [Theory]
    [InlineData(9.9, 1)]
    [InlineData(10.0, 1)]   // boundary: exactly 10 nm still chains
    [InlineData(10.1, 2)]
    public void PortCallRadiusBoundary(double separationNm, int expectedCalls)
    {
        var calls = new PortCallChainer().Chain([
            Stop(0, 5, 0.002, lat: 56.0),
            Stop(8, 5, 0.002, lat: 56.0 + Nm(separationNm)),
        ]);

        Assert.Equal(expectedCalls, calls.Count);
    }

    [Fact]
    public void APortCallIsOnlyAsCompleteAsItsLeastCompleteStop()
    {
        // Waiting and working hours cannot be trusted if any phase ran off the edge of the
        // window, so the flag has to propagate rather than being computed per phase.
        var calls = new PortCallChainer().Chain([
            Stop(0, 5, 0.09) with { IsComplete = false },
            Stop(8, 5, 0.002),
        ]);

        Assert.Single(calls);
        Assert.False(calls[0].IsComplete);
    }

    [Fact]
    public void ArrivalAndDepartureSpanTheWholeCall()
    {
        var call = Assert.Single(new PortCallChainer().Chain([
            Stop(2, 5, 0.09),
            Stop(9, 6, 0.002),
        ]));

        Assert.Equal(T0.AddHours(2), call.ArrivedUtc);
        Assert.Equal(T0.AddHours(15), call.DepartedUtc);
    }

    [Fact]
    public void AStopWithOneSurvivingFixIsNotClassifiedAsABerth()
    {
        // The collapsed-geometry defect: with a single reliable fix the centroid IS that fix,
        // so drift computes to exactly 0.0 -- the most confident possible berth value, on the
        // stops most likely to have been a drifting vessel (ADR-0025).
        var collapsed = Stop(0, 10, driftNm: 0.0) with { FixCount = 200, ReliableFixCount = 1 };

        Assert.False(collapsed.GeometryTrustworthy);
        Assert.Equal(StopPhase.Unknown, new PortCallChainer().Classify(collapsed));
    }

    [Fact]
    public void AStopWithMostFixesExcludedIsNotClassified()
    {
        var mostlyExcluded = Stop(0, 10, driftNm: 0.001) with { FixCount = 200, ReliableFixCount = 99 };

        Assert.False(mostlyExcluded.GeometryTrustworthy);
        Assert.Equal(StopPhase.Unknown, new PortCallChainer().Classify(mostlyExcluded));
    }

    [Fact]
    public void AStopWithAMinorityExcludedIsStillClassified()
    {
        // The measured worst case is 27% excluded; that geometry is fine and must not be thrown
        // away by a guard aimed at collapse.
        var mostlyFine = Stop(0, 10, driftNm: 0.001) with { FixCount = 200, ReliableFixCount = 146 };

        Assert.True(mostlyFine.GeometryTrustworthy);
        Assert.Equal(StopPhase.Berth, new PortCallChainer().Classify(mostlyFine));
    }

    [Fact]
    public void UnknownPhasesAreCountedAsNeitherWaitingNorWorking()
    {
        // Folding them into either figure would put a number the data does not support into a
        // laytime calculation.
        var call = Assert.Single(new PortCallChainer().Chain([
            Stop(0, 5, 0.09),
            Stop(6, 4, 0.0) with { FixCount = 100, ReliableFixCount = 1 },
        ]));

        Assert.Equal(5.0, call.WaitingHours, 3);
        Assert.Equal(0.0, call.WorkingHours, 3);
        Assert.Equal(4.0, call.UnclassifiedHours, 3);
    }

    [Fact]
    public void NoStopsMeansNoPortCalls() =>
        Assert.Empty(new PortCallChainer().Chain([]));
}
