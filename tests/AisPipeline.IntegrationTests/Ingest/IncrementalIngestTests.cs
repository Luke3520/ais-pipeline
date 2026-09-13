using AisPipeline.Adapters.Csv;
using AisPipeline.Adapters.Sql;
using AisPipeline.Core.Annotate;
using AisPipeline.Core.Detection;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Quality;
using AisPipeline.IntegrationTests.Harness;

namespace AisPipeline.IntegrationTests.Ingest;

/// <summary>
/// A day's file arriving after the store is already populated must land exactly where it would
/// have landed had everything been ingested at once.
///
/// This is the property the whole operating model rests on -- the DMA archive publishes daily with
/// a three-day lag, so every store in real use is built one file at a time -- and nothing asserted
/// it. Idempotency was covered (the same file twice), and batching within a run was covered, but
/// not two DIFFERENT files arriving separately.
///
/// It is not obvious that it holds. The annotate pass judges each fix against its predecessor for
/// the same vessel, and a vessel's fixes are split across the boundary here; detection chains stops
/// into port calls across it; and a stop that was still open at the end of the first file has to be
/// completed by the second rather than left truncated. Splitting the fixture mid-stream exercises
/// all three.
/// </summary>
public class IncrementalIngestTests
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

    /// <summary>
    /// Splits the fixture in two, each half carrying the header the reader needs.
    ///
    /// Split by row count, not by vessel, and that is the point: at 372/372 the halves cover
    /// 00:00:00-02:13:47 and 02:13:47-02:39:48, four vessels appear in both, and the boundary falls
    /// inside a single second. A split that gave each file its own vessels would pass this test
    /// while exercising none of what makes it interesting.
    /// </summary>
    private static (string First, string Second) SplitFixture()
    {
        var lines = File.ReadAllLines(FixturePath());
        var header = lines[0];
        var body = lines[1..];
        var half = body.Length / 2;

        var first = Path.Combine(Path.GetTempPath(), $"ais-first-{Guid.NewGuid():N}.csv");
        var second = Path.Combine(Path.GetTempPath(), $"ais-second-{Guid.NewGuid():N}.csv");

        File.WriteAllLines(first, [header, .. body[..half]]);
        File.WriteAllLines(second, [header, .. body[half..]]);
        return (first, second);
    }

    private static void IngestAndDetect(IStoreHarness harness, params string[] sources)
    {
        foreach (var source in sources)
        {
            using var store = harness.Create();
            new IngestPipeline(store, RuleRegistry.Default(), new IngestOptions())
                .Run(new DmaCsvSource(source));
        }

        using var detectStore = harness.Create();
        new AnnotatePass(detectStore, RuleRegistry.Default().SequenceRules).Run();
        new DetectionPass(detectStore).Run();
    }

    /// <summary>The numbers a reader of this project would actually quote.</summary>
    private static Dictionary<string, string> Fingerprint(IStoreHarness harness)
    {
        using var queries = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect);
        var stops = queries.ListStops(new Core.Query.StopFilter { Limit = 5_000 });
        var calls = queries.ListPortCalls(new Core.Query.PortCallFilter { Limit = 5_000 });

        return new Dictionary<string, string>
        {
            ["positions"] = harness.Count("position_report").ToString(),
            ["vessels"] = harness.Count("vessel").ToString(),
            ["quarantine"] = harness.Count("quarantine").ToString(),
            ["stops"] = stops.Count.ToString(),
            ["completeStops"] = stops.Count(s => s.IsComplete).ToString(),
            ["portCalls"] = calls.Count.ToString(),
            ["disagreeing"] = queries.StatusDisagreement().Disagreeing.ToString(),
            ["stopHours"] = stops.Sum(s => s.ObservedDurationHours).ToString("F6"),
            ["waitingHours"] = calls.Sum(c => c.WaitingHours).ToString("F6"),
            ["workingHours"] = calls.Sum(c => c.WorkingHours).ToString("F6"),
            ["flags"] = string.Join(
                ",",
                queries.QualityReport().OrderBy(r => r.RuleId, StringComparer.Ordinal)
                    .Select(r => $"{r.RuleId}:{r.Quarantined}/{r.Flagged}")),
        };
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void TwoFilesIngestedSeparatelyLandWhereOneIngestWould(Func<IStoreHarness> make)
    {
        var (first, second) = SplitFixture();

        try
        {
            using var wholeHarness = make();
            IngestAndDetect(wholeHarness, FixturePath());
            var whole = Fingerprint(wholeHarness);

            using var splitHarness = make();
            IngestAndDetect(splitHarness, first, second);
            var split = Fingerprint(splitHarness);

            Assert.Equal(whole, split);

            // Guard against the comparison passing because both sides are empty.
            Assert.NotEqual("0", whole["positions"]);
            Assert.NotEqual("0", whole["stops"]);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void TheSecondFileArrivingTwiceChangesNothing(Func<IStoreHarness> make)
    {
        // Idempotency on a store that already holds data, which is the real operating case -- the
        // existing coverage re-runs the same file into a store that file alone built.
        var (first, second) = SplitFixture();

        try
        {
            using var harness = make();
            IngestAndDetect(harness, first, second);
            var once = Fingerprint(harness);

            IngestAndDetect(harness, second);
            Assert.Equal(once, Fingerprint(harness));
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }
}
