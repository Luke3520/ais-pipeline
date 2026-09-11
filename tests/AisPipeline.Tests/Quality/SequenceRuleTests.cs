using AisPipeline.Core.Domain;
using AisPipeline.Core.Geo;
using AisPipeline.Core.Quality;
using AisPipeline.Core.Quality.Rules;

namespace AisPipeline.Tests.Quality;

public class SequenceRuleTests
{
    private static readonly DateTime T0 = new(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);

    private static PositionFix Fix(double seconds, double lat, double lon, double? sog = 5.0) => new()
    {
        Id = (long)seconds + 1,
        Mmsi = 219000001,
        TimestampUtc = T0.AddSeconds(seconds),
        Latitude = lat,
        Longitude = lon,
        SpeedOverGroundKn = sog,
    };

    /// <summary>Latitude offset for a given distance in nautical miles (1' of latitude = 1 nm).</summary>
    private static double Nm(double nm) => nm / (Units.EarthRadiusNm * Math.PI / 180.0);

    // ---- R7 ----

    [Fact]
    public void R7FlagsAJumpThatIsBothFastAndFar()
    {
        // The real case: MMSI 266473000 moved 3,040 nm in 10 seconds, three times in one day.
        var hit = new R7Teleport().Evaluate(
            Fix(0, 56.0, 10.0),
            Fix(10, 14.0, 9.0));

        Assert.NotNull(hit);
        Assert.Equal("R7", hit!.RuleId);
        Assert.Equal(RuleAction.Flag, hit.Action);
    }

    [Fact]
    public void R7IgnoresGpsScatterHoweverFastItImplies()
    {
        // 60 m in 2 s implies ~58 kn and is the median of what a speed-only rule flags. 87% of
        // its 2,084 daily hits look like this, and detection would exclude every one of them
        // from the drift geometry that classifies berths (ADR-0023).
        var hit = new R7Teleport().Evaluate(
            Fix(0, 56.0, 10.0),
            Fix(2, 56.0 + Nm(60.0 / 1852.0), 10.0));

        Assert.Null(hit);
    }

    // The boundary is pinned by the values either side of it rather than at it. A distance of
    // exactly 0.5 nm is not constructible through coordinates: offsetting latitude by 0.5 nm and
    // measuring it back yields 0.5000000000000808, which is above the gate, not at it. Asserting
    // "exactly 0.5 is not flagged" would be testing floating-point round-trip, not the rule.
    [Theory]
    [InlineData(0.4999, false)]
    [InlineData(0.5001, true)]
    public void R7DistanceGateBoundary(double distanceNm, bool flagged)
    {
        // One second, so implied speed is enormous either way; only distance decides.
        var hit = new R7Teleport().Evaluate(
            Fix(0, 56.0, 10.0),
            Fix(1, 56.0 + Nm(distanceNm), 10.0));

        Assert.Equal(flagged, hit is not null);
    }

    [Fact]
    public void R7DoesNotFireWhenTheDistanceIsFarButTheSpeedIsPlausible()
    {
        // 5 nm in an hour is 5 kn. Far, but exactly what a ship does.
        Assert.Null(new R7Teleport().Evaluate(
            Fix(0, 56.0, 10.0),
            Fix(3600, 56.0 + Nm(5.0), 10.0)));
    }

    [Fact]
    public void R7DoesNotDivideByZeroWhenTwoFixesShareASecond()
    {
        // 0.40% of consecutive pairs share a vessel-second. Implied speed is undefined, not
        // infinite, and an unguarded division would flag every one of them.
        Assert.Null(new R7Teleport().Evaluate(
            Fix(0, 56.0, 10.0),
            Fix(0, 56.0 + Nm(5.0), 10.0)));
    }

    // ---- R8 ----

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, false)]   // boundary: exactly 60 minutes is not yet a gap
    [InlineData(61, true)]
    public void R8CoverageGapBoundary(int minutes, bool flagged)
    {
        var hit = new R8CoverageGap().Evaluate(
            Fix(0, 56.0, 10.0),
            Fix(minutes * 60, 56.0, 10.0));

        Assert.Equal(flagged, hit is not null);
    }

    [Fact]
    public void R8ReportsHowLongTheSilenceLasted()
    {
        var hit = new R8CoverageGap().Evaluate(Fix(0, 56.0, 10.0), Fix(7200, 56.0, 10.0));

        Assert.Contains("120 min", hit!.Detail, StringComparison.Ordinal);
    }

    // ---- R11 ----

    [Fact]
    public void R11CatchesAVesselThatClaimsToBeStillWhileMoving()
    {
        // The case R7 structurally cannot see: one sampled stop drifted 11.09 nm while
        // reporting "Moored" throughout, never fast enough to trip a 50 kn threshold.
        var hit = new R11SpeedConsistency().Evaluate(
            Fix(0, 56.0, 10.0, sog: 0.0),
            Fix(3600, 56.0 + Nm(11.0), 10.0));

        Assert.NotNull(hit);
        Assert.Equal("R11", hit!.RuleId);
    }

    // As with R7, the exact threshold is not reachable through a coordinate round-trip, so the
    // boundary is pinned by the closest representable values on each side.
    [Theory]
    [InlineData(0.0999, false)]
    [InlineData(0.1001, true)]
    public void R11DistanceGateBoundary(double distanceNm, bool flagged)
    {
        // Without the gate the rule produced 35,303 hits over seven days with a median
        // displacement of 57 metres -- GPS scatter at a 2-second reporting interval, the same
        // defect ADR-0023 fixed for R7 (ADR-0025).
        var hit = new R11SpeedConsistency().Evaluate(
            Fix(0, 56.0, 10.0, sog: 0.0),
            Fix(10, 56.0 + Nm(distanceNm), 10.0));

        Assert.Equal(flagged, hit is not null);
    }

    [Fact]
    public void R11IsMoreSensitiveThanR7ByDesign()
    {
        // R11 exists to catch movement too slow to trip a teleport threshold, so its gate must
        // sit below R7's or it can never see anything R7 does not.
        Assert.True(R11SpeedConsistency.MinimumDistanceNm < R7Teleport.MinimumDistanceNm);
    }

    [Fact]
    public void R11IgnoresSmallDisagreementsNearZero()
    {
        // Reported 0.1 kn against an implied 0.3 kn is threefold and meaningless. The absolute
        // margin is what stops the ratio firing constantly on a moored vessel.
        Assert.Null(new R11SpeedConsistency().Evaluate(
            Fix(0, 56.0, 10.0, sog: 0.1),
            Fix(3600, 56.0 + Nm(0.3), 10.0)));
    }

    [Fact]
    public void R11AcceptsAVesselMovingAtRoughlyTheSpeedItReports()
    {
        Assert.Null(new R11SpeedConsistency().Evaluate(
            Fix(0, 56.0, 10.0, sog: 10.0),
            Fix(3600, 56.0 + Nm(10.2), 10.0)));
    }

    [Fact]
    public void R11SaysNothingWhenNoSpeedWasReported()
    {
        // There is no claim to contradict. R5 already flagged the absence.
        Assert.Null(new R11SpeedConsistency().Evaluate(
            Fix(0, 56.0, 10.0, sog: null),
            Fix(3600, 56.0 + Nm(50.0), 10.0)));
    }

    [Fact]
    public void R11DoesNotDivideByZeroWhenTwoFixesShareASecond()
    {
        Assert.Null(new R11SpeedConsistency().Evaluate(
            Fix(0, 56.0, 10.0, sog: 0.0),
            Fix(0, 56.0 + Nm(5.0), 10.0)));
    }

    // ---- status mapping ----

    [Theory]
    [InlineData("Under way using engine", ReportedActivity.UnderWay)]
    [InlineData("Constrained by her draught", ReportedActivity.UnderWay)]
    [InlineData("Moored", ReportedActivity.Stationary)]
    [InlineData("At anchor", ReportedActivity.Stationary)]
    [InlineData("Unknown value", ReportedActivity.Unknown)]
    [InlineData("Not under command", ReportedActivity.Unknown)]
    [InlineData(null, ReportedActivity.Unknown)]
    [InlineData("something DMA has not emitted before", ReportedActivity.Unknown)]
    public void StatusMapping(string? status, ReportedActivity expected) =>
        Assert.Equal(expected, NavigationalStatus.Classify(status));
}
