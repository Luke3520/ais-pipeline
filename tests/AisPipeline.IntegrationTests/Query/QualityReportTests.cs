using AisPipeline.Adapters.Csv;
using AisPipeline.Adapters.Sql;
using AisPipeline.Core.Annotate;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Quality;
using AisPipeline.IntegrationTests.Harness;

namespace AisPipeline.IntegrationTests.Query;

/// <summary>
/// The quality report over a real ingest of the fixture.
///
/// Unit tests cover the reconciliation of counts against the registry; what they cannot cover is
/// whether the adapter can see the flagged half at all. Flags are a comma-separated string on
/// <c>position_report</c>, not rows in a table, so counting them is a decomposition rather than a
/// GROUP BY — and getting it wrong returns a plausible number, not an error (ADR-0032).
/// </summary>
public class QualityReportTests
{
    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AisPipeline.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "fixtures", "sample.csv");
    }

    private static void Populate(IStoreHarness harness)
    {
        using (var store = harness.Create())
        {
            // No ship-type filter. The quality report is a census of the feed, and the fixture is
            // cut to exercise every rule across the whole of it (fixtures/MANIFEST.md); scoping
            // to tankers here would leave the flagging rules with nothing to fire on.
            new IngestPipeline(store, RuleRegistry.Default(), new IngestOptions())
                .Run(new DmaCsvSource(FixturePath()));
        }

        using var annotateStore = harness.Create();
        new AnnotatePass(annotateStore, RuleRegistry.Default().SequenceRules).Run();
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void FlaggedRowsAreCountedNotJustQuarantinedOnes(Func<IStoreHarness> make)
    {
        using var harness = make();
        Populate(harness);
        using var queries = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect);

        var report = QualityReport.Build(RuleRegistry.Default(), queries.QualityReport());

        // The fixture is cut to exercise every rule (fixtures/MANIFEST.md), so at least one
        // rejecting rule and at least one flagging rule must show a count. Before this change the
        // second assertion was unsatisfiable: the flagging rules were absent from the report.
        Assert.Contains(report, l => l.Quarantined > 0);
        Assert.Contains(report, l => l.Flagged > 0);
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void EveryFlagWrittenToARowIsAttributedToItsRule(Func<IStoreHarness> make)
    {
        using var harness = make();
        Populate(harness);
        using var queries = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect);

        var report = QualityReport.Build(RuleRegistry.Default(), queries.QualityReport());

        // A row can carry several flags, so the per-rule counts must sum to the total number of
        // flags written, not to the number of flagged rows. Splitting the comma-separated column
        // wrongly -- taking only the first id, say -- undercounts here and nowhere else.
        // Derived independently of the adapter: count the separators in the column rather than
        // reusing the same split, so a bug in the split cannot satisfy both sides.
        var flagsOnRows = harness.Scalar("""
            SELECT CAST(SUM(LENGTH(quality_flags) - LENGTH(REPLACE(quality_flags, ',', '')) + 1) AS BIGINT)
            FROM position_report
            WHERE quality_flags <> ''
            """);

        Assert.Equal(flagsOnRows, report.Sum(l => l.Flagged));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void TheReportNamesEveryRegisteredRuleWhateverTheDataContains(Func<IStoreHarness> make)
    {
        using var harness = make();
        Populate(harness);
        using var queries = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect);

        var report = QualityReport.Build(RuleRegistry.Default(), queries.QualityReport());

        Assert.Equal(
            RuleRegistry.Default().All.Select(r => r.Id).OrderBy(id => id).ToList(),
            report.Select(l => l.RuleId).OrderBy(id => id).ToList());
    }
}
