using AisPipeline.Core.Domain;
using AisPipeline.Core.Quality;
using AisPipeline.Core.Quality.Rules;

namespace AisPipeline.Tests.Quality;

/// <summary>
/// R13: an ETA too far ahead to be a plan.
///
/// The third hand-entered field checked, and the one that goes stale backwards: the AIS ETA has no
/// year, so a decoder rolls a passed date forward and a forgotten ETA reappears in the future
/// rather than the past (ADR-0041).
/// </summary>
public class R13EtaImplausiblyFarAheadTests
{
    private static readonly R13EtaImplausiblyFarAhead Rule = new();
    private static readonly DateTime Fix = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static RawAisRecord Record(DateTime? eta) => new()
    {
        SourceFile = "test.csv",
        SourceLine = 1,
        RawText = "",
        TimestampUtc = Fix,
        TypeOfMobile = "Class A",
        Mmsi = 219000001,
        Latitude = 56.0,
        Longitude = 10.0,
        NavigationalStatus = "Under way using engine",
        EtaUtc = eta,
    };

    [Fact]
    public void An_eta_rolled_over_to_next_year_is_flagged()
    {
        // The observed signature: a cluster at about 364 days, which is a date that passed being
        // read as its next occurrence.
        var hit = Rule.Evaluate(Record(Fix.AddDays(364)));

        Assert.NotNull(hit);
        Assert.Equal(RuleIds.EtaImplausiblyFarAhead, hit!.RuleId);
        Assert.Equal(RuleAction.Flag, hit.Action);
    }

    [Fact]
    public void It_flags_and_never_rejects()
    {
        // A stale ETA says nothing about the position on the row, which is measured and sound.
        Assert.Equal(RuleAction.Flag, Rule.Evaluate(Record(Fix.AddDays(364)))!.Action);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    [InlineData(30)]
    public void A_real_voyage_eta_is_left_alone(int days)
    {
        // 95,075 of 120,225 observed ETAs fall within 14 days and 12,526 more within 30.
        Assert.Null(Rule.Evaluate(Record(Fix.AddDays(days))));
    }

    [Fact]
    public void An_eta_in_the_past_is_not_this_rule_s_business()
    {
        // A ship running late is not a data defect, and every forecast is sometimes wrong. The
        // furthest into the past observed was six hours. Flagging lateness would bury the finding.
        Assert.Null(Rule.Evaluate(Record(Fix.AddHours(-6))));
        Assert.Null(Rule.Evaluate(Record(Fix.AddDays(-2))));
    }

    [Fact]
    public void The_boundary_is_exclusive()
    {
        // Exactly at the threshold does not fire; a day beyond it does.
        //
        // The stronger claim behind the constant -- that any threshold between 30 and 90 days
        // classifies the same rows -- is a property of the DATA, not of this method, and is
        // recorded in ADR-0041 with the distribution it came from. A synthetic record at 89 days
        // does fire, correctly, because the threshold is 60; asserting otherwise here would encode
        // a false claim as a passing test.
        Assert.Null(Rule.Evaluate(Record(Fix.AddDays(R13EtaImplausiblyFarAhead.ImplausibleAfterDays))));
        Assert.NotNull(Rule.Evaluate(Record(Fix.AddDays(R13EtaImplausiblyFarAhead.ImplausibleAfterDays + 1))));
    }

    [Fact]
    public void No_eta_is_not_a_finding()
    {
        Assert.Null(Rule.Evaluate(Record(null)));
    }

    [Fact]
    public void The_rule_is_registered_so_ais_quality_reports_it()
    {
        Assert.Contains(RuleIds.EtaImplausiblyFarAhead, RuleRegistry.Default().All.Select(r => r.Id));
    }
}
