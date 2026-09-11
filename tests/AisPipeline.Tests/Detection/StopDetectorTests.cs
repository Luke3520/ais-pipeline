using AisPipeline.Core.Detection;
using AisPipeline.Core.Domain;

namespace AisPipeline.Tests.Detection;

/// <summary>
/// Hand-built sequences. A fixture can only demonstrate the cases it happens to contain; these
/// pin the ones that decide whether a stored stop is real, including the boundary values of
/// every threshold the state machine turns on.
/// </summary>
public class StopDetectorTests
{
    private static readonly DateTime T0 = new(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);

    private static PositionFix Fix(
        int minutes,
        double? sog,
        double lat = 56.0,
        double lon = 10.0,
        string status = "Moored",
        string flags = "",
        long id = 0) => new()
        {
            Id = id == 0 ? minutes + 1 : id,
            Mmsi = 219000001,
            TimestampUtc = T0.AddMinutes(minutes),
            Latitude = lat,
            Longitude = lon,
            SpeedOverGroundKn = sog,
            NavigationalStatus = status,
            QualityFlags = flags,
        };

    /// <summary>Fixes every minute from <paramref name="from"/> to <paramref name="to"/>.</summary>
    private static IEnumerable<PositionFix> Span(
        int from, int to, double? sog, double lat = 56.0, double lon = 10.0, string status = "Moored")
    {
        for (var m = from; m <= to; m++)
        {
            yield return Fix(m, sog, lat, lon, status);
        }
    }

    private static IReadOnlyList<StopEvent> Detect(params IEnumerable<PositionFix>[] parts) =>
        new StopDetector().Detect([.. parts.SelectMany(p => p)]);

    // ---- the canonical sequence ----

    [Fact]
    public void MovingThenStoppedThenMovingYieldsOneCompleteStop()
    {
        var stops = Detect(
            Span(0, 10, 8.0, status: "Under way using engine"),
            Span(11, 60, 0.1),
            Span(61, 70, 8.0, status: "Under way using engine"));

        var stop = Assert.Single(stops);
        Assert.True(stop.IsComplete);
        Assert.Equal(T0.AddMinutes(11), stop.StartedUtc);
        Assert.InRange(stop.DurationHours, 0.8, 0.85);
    }

    [Fact]
    public void TwoSeparateStopsAreNotMergedIntoOne()
    {
        var stops = Detect(
            Span(0, 5, 8.0),
            Span(6, 45, 0.1),
            Span(46, 60, 8.0),
            Span(61, 120, 0.1),
            Span(121, 130, 8.0));

        Assert.Equal(2, stops.Count);
    }

    // ---- hysteresis ----

    [Fact]
    public void BobbingBetweenTheThresholdsProducesOneStopNotFifty()
    {
        // 0.7 kn sits between enter (0.5) and leave (1.0). Without hysteresis each excursion
        // would close and reopen a stop; with it, the state simply does not change.
        var fixes = new List<PositionFix>();
        fixes.AddRange(Span(0, 5, 8.0));
        for (var m = 6; m <= 60; m++)
        {
            fixes.Add(Fix(m, m % 2 == 0 ? 0.2 : 0.7));
        }

        fixes.AddRange(Span(61, 70, 8.0));

        Assert.Single(new StopDetector().Detect(fixes));
    }

    [Theory]
    [InlineData(0.49, true)]   // below enter: starts a stop
    [InlineData(0.5, false)]   // boundary: NOT below 0.5, so no stop starts
    [InlineData(0.51, false)]
    public void EnterThresholdBoundary(double sog, bool stopExpected)
    {
        var stops = Detect(Span(0, 5, 8.0), Span(6, 60, sog), Span(61, 70, 8.0));

        Assert.Equal(stopExpected, stops.Count == 1);
    }

    [Theory]
    [InlineData(0.99, false)]  // below leave: the stop continues, so it is never closed
    [InlineData(1.0, true)]    // boundary: at exactly 1.0 the vessel is moving again
    public void LeaveThresholdBoundary(double sog, bool closesStop)
    {
        var stops = Detect(Span(0, 5, 8.0), Span(6, 60, 0.1), Span(61, 90, sog));

        Assert.Single(stops);
        Assert.Equal(closesStop, stops[0].IsComplete);
    }

    // ---- minimum duration ----

    [Theory]
    [InlineData(29, 0)]   // below the minimum
    [InlineData(30, 1)]   // boundary: exactly 30 minutes is emitted
    [InlineData(31, 1)]
    public void MinimumDurationBoundary(int stoppedMinutes, int expected)
    {
        var stops = Detect(
            Span(0, 5, 8.0),
            Span(6, 6 + stoppedMinutes, 0.1),
            Span(7 + stoppedMinutes, 20 + stoppedMinutes, 8.0));

        Assert.Equal(expected, stops.Count);
    }

    // ---- coverage gaps ----

    [Fact]
    public void ACoverageGapClosesTheStopAndMarksItIncomplete()
    {
        // The defect this prevents: a vessel that leaves receiver range at low speed and
        // returns two days later would otherwise be recorded as stopped for two days.
        var fixes = new List<PositionFix>();
        fixes.AddRange(Span(0, 5, 8.0));
        fixes.AddRange(Span(6, 60, 0.1));
        fixes.Add(Fix(60 + (60 * 48), 0.1));   // reappears two days later

        var stops = new StopDetector().Detect(fixes);

        Assert.Single(stops);
        Assert.False(stops[0].IsComplete);
        Assert.InRange(stops[0].DurationHours, 0.8, 1.0);
    }

    [Theory]
    [InlineData(59, 1)]   // below the gap threshold: one continuous stop
    [InlineData(60, 1)]   // boundary: exactly 60 minutes is NOT a gap
    [InlineData(61, 2)]   // first value over: the stop is split
    public void CoverageGapBoundary(int gapMinutes, int expectedStops)
    {
        var fixes = new List<PositionFix>();
        fixes.AddRange(Span(0, 40, 0.1));
        fixes.AddRange(Span(40 + gapMinutes, 80 + gapMinutes, 0.1));

        Assert.Equal(expectedStops, new StopDetector().Detect(fixes).Count);
    }

    // ---- unavailable speed ----

    [Fact]
    public void UnavailableSpeedDoesNotStartAStop()
    {
        // Coalescing null to zero would fabricate a stop out of a vessel steaming past with a
        // broken speed sensor.
        Assert.Empty(Detect(Span(0, 5, 8.0), Span(6, 90, null), Span(91, 100, 8.0)));
    }

    [Fact]
    public void UnavailableSpeedDoesNotEndAStopEither()
    {
        // The vessel is still there -- the fix proves presence, just not motion. Treating the
        // absence as movement would truncate a real stop at an arbitrary point.
        var stops = Detect(
            Span(0, 5, 8.0),
            Span(6, 30, 0.1),
            Span(31, 40, null),
            Span(41, 70, 0.1),
            Span(71, 80, 8.0));

        var stop = Assert.Single(stops);
        Assert.Equal(T0.AddMinutes(70), stop.EndedUtc);
    }

    // ---- window edges ----

    [Fact]
    public void AStopAlreadyUnderWayAtTheFirstFixIsIncomplete()
    {
        // ~One third of tankers are stationary across any given midnight, so this is the
        // common case rather than an edge case.
        var stops = Detect(Span(0, 60, 0.1), Span(61, 70, 8.0));

        Assert.Single(stops);
        Assert.False(stops[0].IsComplete);
    }

    [Fact]
    public void AStopStillRunningAtTheLastFixIsIncomplete()
    {
        var stops = Detect(Span(0, 5, 8.0), Span(6, 90, 0.1));

        Assert.Single(stops);
        Assert.False(stops[0].IsComplete);
    }

    // ---- geometry ----

    [Fact]
    public void DriftIsMeasuredFromTheCentroidOfTheStoppedFixes()
    {
        var fixes = new List<PositionFix> { Fix(0, 8.0) };
        for (var m = 1; m <= 60; m++)
        {
            // ~0.5' of latitude either side of 56.0 => ~0.5 nm from the centroid.
            fixes.Add(Fix(m, 0.1, lat: m % 2 == 0 ? 56.0 - (0.5 / 60) : 56.0 + (0.5 / 60)));
        }

        fixes.AddRange(Span(61, 70, 8.0));

        var stop = Assert.Single(new StopDetector().Detect(fixes));
        Assert.Equal(56.0, stop.CentroidLatitude, 4);
        Assert.Equal(0.5, stop.MaxDriftNm, 1);
    }

    [Fact]
    public void APositionallyUnreliableFixIsExcludedFromTheGeometryButStillCounted()
    {
        // One corrupt position otherwise drags the centroid AND supplies the maximum, which is
        // how a single bad fix produced 2,387 nm of drift across hundreds of good ones.
        var fixes = new List<PositionFix> { Fix(0, 8.0) };
        fixes.AddRange(Span(1, 60, 0.1));
        fixes.Add(Fix(30, 0.1, lat: 14.0, lon: 9.0, flags: "R7", id: 9_999));
        fixes.AddRange(Span(61, 70, 8.0));
        fixes = [.. fixes.OrderBy(f => f.TimestampUtc).ThenBy(f => f.Id)];

        var stop = Assert.Single(new StopDetector().Detect(fixes));

        Assert.Equal(56.0, stop.CentroidLatitude, 6);
        Assert.True(stop.MaxDriftNm < 0.001, $"drift {stop.MaxDriftNm} was poisoned by the bad fix");
        Assert.Contains(fixes, f => f.Id == 9_999);
        Assert.Equal(61, stop.FixCount);
    }

    // ---- R10 ----

    [Fact]
    public void StatusDisagreesWhenAStationaryVesselClaimsToBeUnderWay()
    {
        var stop = Assert.Single(Detect(
            Span(0, 5, 8.0),
            Span(6, 60, 0.1, status: "Under way using engine"),
            Span(61, 70, 8.0)));

        Assert.False(stop.StatusAgrees);
        Assert.Equal("Under way using engine", stop.ReportedStatus);
    }

    [Theory]
    [InlineData("Moored", true)]
    [InlineData("At anchor", true)]
    [InlineData("Aground", true)]
    [InlineData("Under way using engine", false)]
    [InlineData("Under way sailing", false)]
    [InlineData("Constrained by her draught", false)]
    [InlineData("Unknown value", true)]
    public void StatusAgreementFollowsTheReportedActivity(string status, bool agrees)
    {
        var stop = Assert.Single(Detect(
            Span(0, 5, 8.0), Span(6, 60, 0.1, status: status), Span(61, 70, 8.0)));

        Assert.Equal(agrees, stop.StatusAgrees);
    }

    [Fact]
    public void ProvenancePointsAtTheFirstAndLastFixOfTheStop()
    {
        var stop = Assert.Single(Detect(Span(0, 5, 8.0), Span(6, 60, 0.1), Span(61, 70, 8.0)));

        Assert.Equal(7, stop.FirstPositionId);
        Assert.Equal(61, stop.LastPositionId);
    }

    [Fact]
    public void NoFixesMeansNoStops() =>
        Assert.Empty(new StopDetector().Detect([]));
}
