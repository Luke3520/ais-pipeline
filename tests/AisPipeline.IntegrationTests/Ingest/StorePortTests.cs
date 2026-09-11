using AisPipeline.Adapters.Csv;
using AisPipeline.Core.Annotate;
using AisPipeline.Core.Detection;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Quality;
using AisPipeline.Core.Quality.Rules;
using AisPipeline.IntegrationTests.Harness;

namespace AisPipeline.IntegrationTests.Ingest;

/// <summary>
/// The guarantees that no unit test can establish, run against every adapter.
///
/// Idempotency is enforced by a database constraint rather than a pure function, so it has to be
/// proven against a real engine (ADR-0005) -- and against each engine, because "the ports make
/// the adapters interchangeable" is a claim, not a fact, until both are exercised by the same
/// assertions.
/// </summary>
public class StorePortTests
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

    private static IngestResult Ingest(IStoreHarness harness)
    {
        using var store = harness.Create();
        var pipeline = new IngestPipeline(
            store, RuleRegistry.Default(), new IngestOptions { ShipType = "Tanker" });
        return pipeline.Run(new DmaCsvSource(FixturePath()));
    }

    private static DetectionResult Detect(IStoreHarness harness)
    {
        using var store = harness.Create();
        new AnnotatePass(store, [new R7Teleport(), new R8CoverageGap(), new R11SpeedConsistency()]).Run();
        return new DetectionPass(store).Run();
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void SecondIngestOfTheSameFileInsertsNothing(Func<IStoreHarness> make)
    {
        using var harness = make();

        var first = Ingest(harness);
        Assert.True(first.Counters.RowsInserted > 0, $"{harness.Name}: first run stored nothing");

        var second = Ingest(harness);

        Assert.Equal(0, second.Counters.RowsInserted);
        Assert.Equal(first.Counters.RowsInserted, second.Counters.RowsDuplicatePriorRun);
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void SecondIngestLeavesEveryTableTheSameSize(Func<IStoreHarness> make)
    {
        using var harness = make();
        Ingest(harness);
        var positions = harness.Count("position_report");
        var quarantined = harness.Count("quarantine");
        var vessels = harness.Count("vessel");

        Ingest(harness);

        Assert.Equal(positions, harness.Count("position_report"));

        // quarantine is the side door: without its uniqueness constraint the positions stay flat
        // while the refusals double, and the guarantee reads as holding while being false.
        Assert.Equal(quarantined, harness.Count("quarantine"));
        Assert.Equal(vessels, harness.Count("vessel"));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void EveryRowReadIsAccountedForOnBothRuns(Func<IStoreHarness> make)
    {
        using var harness = make();

        var first = Ingest(harness);
        var second = Ingest(harness);

        Assert.True(first.Counters.IsBalanced, $"{harness.Name} run 1: {first.Counters}");
        Assert.True(second.Counters.IsBalanced, $"{harness.Name} run 2: {second.Counters}");
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void EachRunIsRecordedSeparatelyEvenThoughItStoredNothing(Func<IStoreHarness> make)
    {
        using var harness = make();
        Ingest(harness);
        Ingest(harness);

        Assert.Equal(2, harness.Count("ingest_run"));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void EveryStoredPositionResolvesToItsIngestRunAndSourceLine(Func<IStoreHarness> make)
    {
        using var harness = make();
        Ingest(harness);

        Assert.Equal(0, harness.CountWhere("""
            SELECT COUNT(*) FROM position_report p
            LEFT JOIN ingest_run r ON r.id = p.ingest_run_id
            WHERE r.id IS NULL OR p.source_line <= 0
            """));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void EveryQuarantinedRowResolvesToTheRunThatRefusedIt(Func<IStoreHarness> make)
    {
        using var harness = make();
        Ingest(harness);
        Ingest(harness);

        Assert.Equal(0, harness.CountWhere("""
            SELECT COUNT(*) FROM quarantine q
            LEFT JOIN ingest_run r ON r.id = q.ingest_run_id
            WHERE r.id IS NULL
            """));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void EveryQuarantinedRowKeepsTheEvidenceThatJustifiesTheRefusal(Func<IStoreHarness> make)
    {
        using var harness = make();
        Ingest(harness);

        Assert.Equal(0, harness.CountWhere("""
            SELECT COUNT(*) FROM quarantine
            WHERE rule_id = '' OR raw_snippet = '' OR source_line <= 0 OR detail IS NULL
            """));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void ScopeGuardFiltersByVesselIdentityNotByEachRowsOwnShipType(Func<IStoreHarness> make)
    {
        using var harness = make();
        var scoped = Ingest(harness);

        var literallyLabelled = File.ReadLines(FixturePath())
            .Skip(1)
            .Count(l => l.Split(',') is { Length: 26 } f && f[13] == "Tanker");

        Assert.True(
            scoped.Counters.RowsInserted + scoped.Counters.RowsDuplicateInFile > literallyLabelled,
            $"{harness.Name}: the scope guard appears to be filtering per row");
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void DetectionIsIdempotent(Func<IStoreHarness> make)
    {
        using var harness = make();
        Ingest(harness);

        var first = Detect(harness);
        var stops = harness.Count("stop_event");
        var calls = harness.Count("port_call");
        var phases = harness.Count("port_call_phase");

        var second = Detect(harness);

        Assert.Equal(first.StopsDetected, second.StopsDetected);
        Assert.Equal(stops, harness.Count("stop_event"));
        Assert.Equal(calls, harness.Count("port_call"));
        Assert.Equal(phases, harness.Count("port_call_phase"));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void EveryStopResolvesToTheFixesItWasComputedFrom(Func<IStoreHarness> make)
    {
        using var harness = make();
        Ingest(harness);
        Detect(harness);

        Assert.Equal(0, harness.CountWhere("""
            SELECT COUNT(*) FROM stop_event s
            LEFT JOIN position_report a ON a.id = s.first_position_id
            LEFT JOIN position_report b ON b.id = s.last_position_id
            WHERE a.id IS NULL OR b.id IS NULL
            """));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void AnnotateDoesNotGrowFlagsOnASecondRun(Func<IStoreHarness> make)
    {
        using var harness = make();
        Ingest(harness);
        Detect(harness);
        var flagged = harness.CountWhere(
            "SELECT COUNT(*) FROM position_report WHERE quality_flags <> ''");

        Detect(harness);

        Assert.Equal(flagged, harness.CountWhere(
            "SELECT COUNT(*) FROM position_report WHERE quality_flags <> ''"));
    }
}
