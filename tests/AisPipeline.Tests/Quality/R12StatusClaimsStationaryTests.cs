using AisPipeline.Core.Domain;
using AisPipeline.Core.Quality;
using AisPipeline.Core.Quality.Rules;

namespace AisPipeline.Tests.Quality;

/// <summary>
/// R12: a stationary claim contradicted by the vessel's own speed.
///
/// The mirror of R10. Where R10 asks "it stopped, did the status keep saying under way?", this asks
/// "the status says moored, is it moving?" -- the same human failure on departure instead of
/// arrival (ADR-0038).
/// </summary>
public class R12StatusClaimsStationaryTests
{
    private static readonly R12StatusClaimsStationary Rule = new();

    private static RawAisRecord Record(string status, double? sog) => new()
    {
        SourceFile = "test.csv",
        SourceLine = 1,
        RawText = "",
        TimestampUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        TypeOfMobile = "Class A",
        Mmsi = 219000001,
        Latitude = 56.0,
        Longitude = 10.0,
        NavigationalStatus = status,
        SpeedOverGroundKn = sog,
    };

    [Theory]
    [InlineData("Moored")]
    [InlineData("At anchor")]
    [InlineData("Aground")]
    public void A_stationary_claim_well_above_the_threshold_is_flagged(string status)
    {
        var hit = Rule.Evaluate(Record(status, 12.2));

        Assert.NotNull(hit);
        Assert.Equal(RuleIds.StatusClaimsStationary, hit!.RuleId);
        Assert.Equal(RuleAction.Flag, hit.Action);
        Assert.Contains(status, hit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void It_flags_and_never_rejects()
    {
        // The position and speed are measured by the receiver and are sound; only the hand-typed
        // status is doubtful. Discarding the fix would lose real track to punish a forgotten dial.
        Assert.Equal(RuleAction.Flag, Rule.Evaluate(Record("Moored", 15.5))!.Action);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(2.9)]
    [InlineData(3.0)]
    public void A_moored_vessel_within_tide_and_noise_is_left_alone(double sog)
    {
        // Everything up to and including the threshold. 157,515 fixes sit at exactly zero and
        // 60,618 below half a knot; flagging those would bury the 4,122 that matter.
        Assert.Null(Rule.Evaluate(Record("Moored", sog)));
    }

    [Fact]
    public void The_boundary_is_exclusive_so_exactly_the_threshold_does_not_fire()
    {
        Assert.Null(Rule.Evaluate(Record("Moored", R12StatusClaimsStationary.ContradictedAboveKn)));
        Assert.NotNull(Rule.Evaluate(Record("Moored", R12StatusClaimsStationary.ContradictedAboveKn + 0.1)));
    }

    [Theory]
    [InlineData("Under way using engine")]
    [InlineData("Under way sailing")]
    [InlineData("Constrained by her draught")]
    public void A_vessel_that_admits_to_moving_is_not_this_rule_s_business(string status)
    {
        // Moving while claiming to move is the ordinary case. R10 owns the opposite direction.
        Assert.Null(Rule.Evaluate(Record(status, 12.0)));
    }

    [Theory]
    [InlineData("Not under command")]
    [InlineData("Unknown value")]
    [InlineData("")]
    public void A_status_that_claims_nothing_cannot_be_contradicted(string status)
    {
        // "Not under command" means unable to manoeuvre, which says nothing about making way -- a
        // broken-down ship still drifts, and often fast. Flagging it would invent a finding.
        Assert.Null(Rule.Evaluate(Record(status, 12.0)));
    }

    [Fact]
    public void No_speed_means_nothing_to_contradict_the_claim_with()
    {
        // R5 owns unavailable speed. Treating null as zero here would silently clear the claim.
        Assert.Null(Rule.Evaluate(Record("Moored", null)));
    }

    [Fact]
    public void The_rule_is_registered_so_ais_quality_reports_it_without_extra_wiring()
    {
        Assert.Contains(
            RuleIds.StatusClaimsStationary,
            RuleRegistry.Default().All.Select(r => r.Id));
    }
}
