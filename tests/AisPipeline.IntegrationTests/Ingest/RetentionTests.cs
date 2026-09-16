using AisPipeline.Adapters.Csv;
using AisPipeline.Adapters.Sql;
using AisPipeline.Core.Annotate;
using AisPipeline.Core.Detection;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Quality;
using AisPipeline.IntegrationTests.Harness;

namespace AisPipeline.IntegrationTests.Ingest;

/// <summary>
/// Pruning, and the invariant that makes it possible.
///
/// An earlier design kept the derived layer over a partly-pruned log and failed four separate ways,
/// none of which a fixture-scale test caught (ADR-0044). This one drops projections with the fixes
/// and lets detection rebuild, so everything derived comes from what is still present — and these
/// tests go at the shapes that broke the last attempt rather than the ones that were easy to write:
/// a stop whose fixes straddle the cutoff, and a port call that begins before it and ends after.
/// </summary>
public class RetentionTests
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

    private static void IngestAndDetect(IStoreHarness harness)
    {
        using (var store = harness.Create())
        {
            new IngestPipeline(store, RuleRegistry.Default(), new IngestOptions())
                .Run(new DmaCsvSource(FixturePath()));
        }

        Detect(harness);
    }

    private static void Detect(IStoreHarness harness)
    {
        using var store = harness.Create();
        new AnnotatePass(store, RuleRegistry.Default().SequenceRules).Run();
        new DetectionPass(store).Run();
    }

    /// <summary>
    /// A cutoff that genuinely straddles a port call, which is the case the previous design could
    /// not survive. Asserts that it does, so the test cannot quietly stop covering it.
    /// </summary>
    private static DateTime StraddlingCutoff(IStoreHarness harness)
    {
        using var queries = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect);
        var calls = queries.ListPortCalls(new Core.Query.PortCallFilter { Limit = 5_000 });

        var longest = calls.OrderByDescending(c => c.DepartedUtc - c.ArrivedUtc).First();
        var cutoff = longest.ArrivedUtc.AddSeconds((longest.DepartedUtc - longest.ArrivedUtc).TotalSeconds / 2);

        Assert.Contains(calls, c => c.ArrivedUtc < cutoff && c.DepartedUtc > cutoff);
        return cutoff;
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void PruningRemovesOldFixesAndEveryProjection(Func<IStoreHarness> make)
    {
        using var harness = make();
        IngestAndDetect(harness);

        var before = harness.Count("position_report");
        Assert.True(harness.Count("stop_event") > 0);

        using var store = harness.Create();
        var removed = store.PruneBefore(StraddlingCutoff(harness), 7, "archive/test.json");

        Assert.True(removed > 0);
        Assert.Equal(before - removed, harness.Count("position_report"));

        // Projections go with them: a derived layer over a partly-pruned log is the contradiction
        // that produced four defects last time.
        Assert.Equal(0, harness.Count("stop_event"));
        Assert.Equal(0, harness.Count("port_call"));
        Assert.Equal(0, harness.Count("port_call_phase"));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void DetectionRebuildsCleanlyAcrossACutoffThatStraddlesAPortCall(Func<IStoreHarness> make)
    {
        // The exact shape that broke the previous design, which died on a foreign key, then a
        // second foreign key, then a unique constraint. It must simply work now.
        using var harness = make();
        IngestAndDetect(harness);

        var cutoff = StraddlingCutoff(harness);

        using (var store = harness.Create())
        {
            store.PruneBefore(cutoff, 0, "archive/test.json");
        }

        Detect(harness);

        using var queries = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect);
        var stops = queries.ListStops(new Core.Query.StopFilter { Limit = 5_000 });

        Assert.NotEmpty(stops);

        // Nothing survives that could not be derived from the fixes still held.
        Assert.Equal(0, harness.CountWhere("""
            SELECT COUNT(*) FROM stop_event s
            LEFT JOIN position_report a ON a.id = s.first_position_id
            LEFT JOIN position_report b ON b.id = s.last_position_id
            WHERE a.id IS NULL OR b.id IS NULL
            """));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void DetectionIsStillIdempotentAfterAPrune(Func<IStoreHarness> make)
    {
        using var harness = make();
        IngestAndDetect(harness);

        using (var store = harness.Create())
        {
            store.PruneBefore(StraddlingCutoff(harness), 0, "archive/test.json");
        }

        Detect(harness);
        var stops = harness.Count("stop_event");
        var calls = harness.Count("port_call");

        Detect(harness);

        Assert.Equal(stops, harness.Count("stop_event"));
        Assert.Equal(calls, harness.Count("port_call"));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void NothingIsRemovedWithoutARecordNamingTheArchive(Func<IStoreHarness> make)
    {
        // Rule 2 applied to deletion. The archive path is part of the record because without it a
        // reader of a pruned store cannot find what used to be there.
        using var harness = make();
        IngestAndDetect(harness);

        using var store = harness.Create();
        var removed = store.PruneBefore(StraddlingCutoff(harness), 42, "archive/before-x.json");

        Assert.Equal(1, harness.Count("retention_event"));
        Assert.Equal(removed, harness.Scalar("SELECT fixes_removed FROM retention_event"));
        Assert.Equal(42, harness.Scalar("SELECT port_calls_archived FROM retention_event"));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void PruningTwiceIsHarmless(Func<IStoreHarness> make)
    {
        using var harness = make();
        IngestAndDetect(harness);
        var cutoff = StraddlingCutoff(harness);

        using (var store = harness.Create())
        {
            store.PruneBefore(cutoff, 0, "archive/test.json");
        }

        Detect(harness);
        var fixes = harness.Count("position_report");

        using (var store = harness.Create())
        {
            Assert.Equal(0, store.PruneBefore(cutoff, 0, "archive/test.json"));
        }

        Assert.Equal(fixes, harness.Count("position_report"));
        Assert.Equal(2, harness.Count("retention_event"));
    }
}
