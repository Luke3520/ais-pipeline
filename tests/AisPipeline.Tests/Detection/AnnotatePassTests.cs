using AisPipeline.Core.Annotate;
using AisPipeline.Core.Domain;
using AisPipeline.Core.Quality.Rules;
using AisPipeline.Tests.Ingest;

namespace AisPipeline.Tests.Detection;

public class AnnotatePassTests
{
    private static readonly DateTime T0 = new(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);

    private static PositionFix Fix(
        long id, long mmsi, double seconds, double lat, string flags = "") => new()
        {
            Id = id,
            Mmsi = mmsi,
            TimestampUtc = T0.AddSeconds(seconds),
            Latitude = lat,
            Longitude = 10.0,
            SpeedOverGroundKn = 0.0,
            NavigationalStatus = "Moored",
            QualityFlags = flags,
        };

    private static FakeAisStore StoreWith(params PositionFix[] fixes)
    {
        var store = new FakeAisStore();
        store.Fixes.AddRange(fixes);
        return store;
    }

    private static AnnotatePass PassOver(FakeAisStore store) =>
        new(store, [new R7Teleport(), new R8CoverageGap(), new R11SpeedConsistency()]);

    [Fact]
    public void RunningTwiceLeavesTheSameFlagsRatherThanAppending()
    {
        // Without stripping owned ids first, a second run appends R7 to a fix that already has
        // it and the flag string grows on every run -- so re-running would not be a no-op.
        var store = StoreWith(
            Fix(1, 219000001, 0, 56.0),
            Fix(2, 219000001, 10, 14.0));

        PassOver(store).Run();
        var afterFirst = store.Fixes.Select(f => f.QualityFlags).ToList();

        PassOver(store).Run();

        Assert.Equal(afterFirst, store.Fixes.Select(f => f.QualityFlags));
        Assert.Contains("R7", store.Fixes[1].QualityFlags, StringComparison.Ordinal);
    }

    [Fact]
    public void IngestTimeFlagsSurviveTheAnnotatePass()
    {
        // R5 and R6 are not this pass's to recompute. Overwriting them would erase the record
        // that a fix's speed was never reported.
        var store = StoreWith(
            Fix(1, 219000001, 0, 56.0, flags: "R5"),
            Fix(2, 219000001, 10, 14.0, flags: "R5"));

        PassOver(store).Run();

        Assert.All(store.Fixes, f =>
            Assert.Contains("R5", f.QualityFlags, StringComparison.Ordinal));
    }

    [Fact]
    public void AFlagIsRemovedWhenTheRuleNoLongerFires()
    {
        // Recomputing from scratch has to mean flags can go away, not just accumulate --
        // otherwise a threshold change can never un-flag anything.
        var store = StoreWith(
            Fix(1, 219000001, 0, 56.0, flags: "R7"),
            Fix(2, 219000001, 3600, 56.001, flags: "R7"));

        PassOver(store).Run();

        Assert.All(store.Fixes, f =>
            Assert.DoesNotContain("R7", f.QualityFlags, StringComparison.Ordinal));
    }

    [Fact]
    public void RulesDoNotCompareAcrossVessels()
    {
        // Fixes arrive ordered by mmsi, so the last fix of one vessel sits next to the first of
        // the next. Comparing them would manufacture a teleport at every vessel boundary.
        var store = StoreWith(
            Fix(1, 219000001, 0, 56.0),
            Fix(2, 219000002, 1, 14.0));

        var result = PassOver(store).Run();

        Assert.Empty(result.RuleHits);
        Assert.All(store.Fixes, f => Assert.Equal(string.Empty, f.QualityFlags));
    }

    [Fact]
    public void ExaminesEveryFix()
    {
        var store = StoreWith(
            Fix(1, 219000001, 0, 56.0),
            Fix(2, 219000001, 10, 56.0),
            Fix(3, 219000002, 0, 57.0));

        Assert.Equal(3, PassOver(store).Run().FixesExamined);
    }
}
