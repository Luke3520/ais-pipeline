using AisPipeline.Adapters.Csv;
using AisPipeline.Core.Annotate;
using AisPipeline.Core.Detection;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Quality;
using AisPipeline.Core.Quality.Rules;
using AisPipeline.IntegrationTests.Harness;

namespace AisPipeline.IntegrationTests.Ingest;

/// <summary>
/// Forces the code paths that only appear once a run spans more than one batch.
///
/// The default batch is 5,000 rows and the fixture is 684. Every other test therefore calls
/// <c>InsertPositions</c> exactly once and flushes flag updates only after the read loop has
/// finished — so the staging table is never recreated across transactions, and the annotate pass
/// never writes while a read is still streaming.
///
/// That second path is the entire reason the Postgres adapter opens a separate connection for
/// <c>UpdateQualityFlags</c>: Npgsql permits one active command per connection. On a real seven-day
/// run it executes constantly. Until this file existed, no test reached it, and the suite would
/// have stayed green while `ais detect --postgres` failed on the only data large enough to matter.
/// </summary>
public class BatchBoundaryTests
{
    /// <summary>Small enough that the fixture spans many batches.</summary>
    private const int TinyBatch = 25;

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

    private static IngestResult IngestInTinyBatches(IStoreHarness harness)
    {
        using var store = harness.Create();
        var pipeline = new IngestPipeline(
            store,
            RuleRegistry.Default(),
            new IngestOptions { ShipType = "Tanker", BatchSize = TinyBatch });
        return pipeline.Run(new DmaCsvSource(FixturePath()));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void ManyBatchesStoreExactlyWhatOneBatchWould(Func<IStoreHarness> make)
    {
        using var tiny = make();
        using var normal = make();

        var batched = IngestInTinyBatches(tiny);

        using (var store = normal.Create())
        {
            new IngestPipeline(store, RuleRegistry.Default(),
                new IngestOptions { ShipType = "Tanker" })
                .Run(new DmaCsvSource(FixturePath()));
        }

        // The staging table is created, filled, and dropped once per batch. If a batch leaked
        // rows into the next one, the totals would diverge from the single-batch run.
        Assert.Equal(normal.Count("position_report"), tiny.Count("position_report"));
        Assert.Equal(normal.Count("quarantine"), tiny.Count("quarantine"));
        Assert.True(batched.Counters.IsBalanced, $"{tiny.Name}: {batched.Counters}");
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void ABatchedRunIsStillIdempotent(Func<IStoreHarness> make)
    {
        using var harness = make();

        IngestInTinyBatches(harness);
        var stored = harness.Count("position_report");
        var second = IngestInTinyBatches(harness);

        Assert.Equal(0, second.Counters.RowsInserted);
        Assert.Equal(stored, harness.Count("position_report"));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void FlagsCanBeWrittenWhileTheFixStreamIsStillOpen(Func<IStoreHarness> make)
    {
        // The path the Postgres adapter's second connection exists for. With a batch of 25 over
        // 684 fixes, the annotate pass flushes repeatedly *inside* its read loop rather than once
        // after it, so a write is issued while the reader is still streaming.
        using var harness = make();
        IngestInTinyBatches(harness);

        using (var store = harness.Create())
        {
            var annotated = new AnnotatePass(
                store,
                [new R7Teleport(), new R8CoverageGap(), new R11SpeedConsistency()],
                batchSize: TinyBatch).Run();

            Assert.True(annotated.FixesExamined > TinyBatch,
                "the fixture no longer spans more than one batch; this test proves nothing");
        }

        // Writing mid-stream must not corrupt the stream: detection reads the same fixes back and
        // has to produce the same stops it would otherwise.
        using var detectStore = harness.Create();
        var detected = new DetectionPass(detectStore).Run();

        Assert.True(detected.StopsDetected > 0, $"{harness.Name}: no stops after a batched annotate");
        Assert.Equal(harness.Count("stop_event"), detected.StopsDetected);
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void BatchedAnnotateProducesTheSameFlagsAsASinglePass(Func<IStoreHarness> make)
    {
        using var batched = make();
        using var single = make();

        IngestInTinyBatches(batched);
        IngestInTinyBatches(single);

        using (var store = batched.Create())
        {
            new AnnotatePass(store,
                [new R7Teleport(), new R8CoverageGap(), new R11SpeedConsistency()],
                batchSize: TinyBatch).Run();
        }

        using (var store = single.Create())
        {
            new AnnotatePass(store,
                [new R7Teleport(), new R8CoverageGap(), new R11SpeedConsistency()]).Run();
        }

        Assert.Equal(
            single.CountWhere("SELECT COUNT(*) FROM position_report WHERE quality_flags <> ''"),
            batched.CountWhere("SELECT COUNT(*) FROM position_report WHERE quality_flags <> ''"));
    }
}
