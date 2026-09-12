using AisPipeline.Core.Quality;
using AisPipeline.Core.Query;

namespace AisPipeline.Tests.Quality;

public class QualityReportTests
{
    private static RuleHitCount Count(string ruleId, long quarantined = 0, long flagged = 0) =>
        new() { RuleId = ruleId, Quarantined = quarantined, Flagged = flagged };

    [Fact]
    public void Every_registered_rule_appears_even_when_it_never_fired()
    {
        var registry = RuleRegistry.Default();

        var report = QualityReport.Build(registry, [Count("R1", quarantined: 3)]);

        Assert.Equal(
            registry.All.Select(r => r.Id).OrderBy(id => id).ToList(),
            report.Select(l => l.RuleId).OrderBy(id => id).ToList());
    }

    [Fact]
    public void A_rule_that_never_fired_is_silent_rather_than_absent()
    {
        var report = QualityReport.Build(RuleRegistry.Default(), [Count("R1", quarantined: 3)]);

        var r6 = report.Single(l => l.RuleId == "R6");
        Assert.True(r6.Silent);
        Assert.Equal(0, r6.Total);

        Assert.False(report.Single(l => l.RuleId == "R1").Silent);
    }

    [Fact]
    public void The_flagging_rules_are_reported_not_dropped()
    {
        // The defect ADR-0032 fixes: R7, R8 and R11 flag rather than reject, so a report built
        // from the quarantine table alone showed nothing at all for them.
        var report = QualityReport.Build(
            RuleRegistry.Default(),
            [Count("R7", flagged: 412), Count("R8", flagged: 9_001)]);

        Assert.Equal(412, report.Single(l => l.RuleId == "R7").Flagged);
        Assert.Equal(9_001, report.Single(l => l.RuleId == "R8").Flagged);
        Assert.Equal(0, report.Single(l => l.RuleId == "R7").Quarantined);
    }

    [Fact]
    public void Rejected_and_flagged_are_kept_apart_and_summed_into_a_total()
    {
        // Not collapsed into one number: a quarantined row is out of the time series and a
        // flagged row is still in it, carrying its doubt. The reader needs both facts.
        var report = QualityReport.Build(
            RuleRegistry.Default(),
            [Count("R4", quarantined: 20, flagged: 5)]);

        var line = report.Single(l => l.RuleId == "R4");
        Assert.Equal(20, line.Quarantined);
        Assert.Equal(5, line.Flagged);
        Assert.Equal(25, line.Total);
    }

    [Fact]
    public void An_id_in_the_data_that_no_rule_owns_is_surfaced_not_hidden()
    {
        // Rows carrying an id nothing currently runs means a rule was retired or renamed without
        // migrating its rows. Dropping the line would orphan those rows silently -- rule 2.
        var report = QualityReport.Build(RuleRegistry.Default(), [Count("R2", quarantined: 77)]);

        var orphan = report.Single(l => l.RuleId == "R2");
        Assert.Equal(77, orphan.Quarantined);
        Assert.Contains("no rule with this id is registered", orphan.Description);
    }

    [Fact]
    public void Rules_are_ordered_numerically_so_R11_does_not_sort_between_R1_and_R4()
    {
        var report = QualityReport.Build(RuleRegistry.Default(), []);

        var ids = report.Select(l => l.RuleId).ToList();
        Assert.Equal(ids.IndexOf("R11"), ids.Count - 1);
        Assert.True(ids.IndexOf("R4") < ids.IndexOf("R11"));
    }

    [Fact]
    public void The_registry_owns_the_sequence_rules_the_annotate_pass_runs()
    {
        // If these two lists ever diverge the report prints a truthful zero for a check that was
        // never performed, which is worse than printing nothing (ADR-0032).
        var sequenceIds = RuleRegistry.Default().SequenceRules.Select(r => r.Id).ToList();

        Assert.Equal(["R7", "R8", "R11"], sequenceIds);
        Assert.All(sequenceIds, id => Assert.Contains(id, RuleRegistry.Default().All.Select(r => r.Id)));
    }
}
