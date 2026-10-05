using AisPipeline.Adapters.Csv;
using AisPipeline.Adapters.Sql;
using AisPipeline.Core.Annotate;
using AisPipeline.Core.Detection;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Quality;
using AisPipeline.Core.Quality.Rules;
using AisPipeline.Core.Query;
using AisPipeline.IntegrationTests.Harness;

namespace AisPipeline.IntegrationTests.Query;

/// <summary>
/// The read side is implemented once for both engines, unlike the schema, which is written twice
/// (ADR-0026). That is a bet that these queries are portable — and the places it could quietly
/// fail are type mappings, not syntax: SQLite has no boolean and stores 0/1, and no date type, so
/// it keeps a sortable ISO string where Postgres keeps an instant.
///
/// A wrong mapping does not throw. It returns a plausible value, which is why these assert on
/// round-tripped content rather than merely that a query ran.
/// </summary>
public class QueryParityTests
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
            new IngestPipeline(store, RuleRegistry.Default(),
                new IngestOptions { ShipType = "Tanker" })
                .Run(new DmaCsvSource(FixturePath()));
        }

        using var detectStore = harness.Create();
        new AnnotatePass(detectStore, RuleRegistry.Default().SequenceRules).Run();
        new DetectionPass(detectStore).Run();
    }

    private static SqlAisQueries QueriesOver(IStoreHarness harness) =>
        new(harness.ConnectionFactory, harness.Dialect);

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void VesselsRoundTripIncludingNullsAndTimestamps(Func<IStoreHarness> make)
    {
        using var harness = make();
        Populate(harness);
        using var queries = QueriesOver(harness);

        var vessels = queries.ListVessels("Tanker", 100);

        Assert.NotEmpty(vessels);
        Assert.All(vessels, v =>
        {
            Assert.Equal("Tanker", v.ShipType);
            Assert.True(v.FirstSeenUtc <= v.LastSeenUtc,
                $"{harness.Name}: mmsi {v.Mmsi} first seen after last seen; a timestamp mapping is wrong");
            Assert.Equal(2026, v.FirstSeenUtc.Year);
        });
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void PortCallsOverlappingReturnsOnlyCallsThatShareTimeWithTheWindow(Func<IStoreHarness> make)
    {
        // The query reconcile selects on (ADR-0035). Overlap is strict on both sides, and the
        // predicate has to mean the same thing under both engines -- SQLite compares timestamps as
        // sortable text, Postgres as instants.
        //
        // This is also the only cover for the timestamp-parameter skew: a DateTime binds under
        // SQLite as "2026-09-05 00:00:00" where the column holds "2026-09-05T00:00:00Z", so a text
        // comparison turns on \' \' sorting below \'T\' and the "inside" case below returns nothing.
        // Every other bounded-window query was deleted with ListFixes; if another is added, it
        // needs SqlDialect.Timestamp and a case like this one.
        using var harness = make();
        Populate(harness);
        using var queries = QueriesOver(harness);

        var all = queries.ListPortCalls(new PortCallFilter { Limit = 500 });
        Assert.NotEmpty(all);

        var subject = all[0];

        // A window strictly inside the call shares time with it.
        var inside = queries.PortCallsOverlapping(
            subject.Mmsi,
            subject.ArrivedUtc.AddMinutes(1),
            subject.DepartedUtc.AddMinutes(-1));
        Assert.Contains(inside, c => c.Id == subject.Id);

        // A window ending exactly when the call starts shares none: touching is not overlapping.
        var touching = queries.PortCallsOverlapping(
            subject.Mmsi,
            subject.ArrivedUtc.AddHours(-5),
            subject.ArrivedUtc);
        Assert.DoesNotContain(touching, c => c.Id == subject.Id);

        // A window a year away shares none either, and every row returned is the right vessel's.
        var elsewhere = queries.PortCallsOverlapping(
            subject.Mmsi,
            subject.ArrivedUtc.AddDays(-365),
            subject.ArrivedUtc.AddDays(-364));
        Assert.Empty(elsewhere);
        Assert.All(inside, c => Assert.Equal(subject.Mmsi, c.Mmsi));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void TrackStreamsTheWholeWindowInTimeOrderAndCarriesProvenance(Func<IStoreHarness> make)
    {
        // The radar's only read of position_report. Every fix in the window, in order, each with
        // the run and line its log line will cite. A mapping that dropped source_line would not
        // throw; it would cite line 0 on every sentence.
        using var harness = make();
        Populate(harness);
        using var queries = QueriesOver(harness);

        var window = queries.FeedWindow();
        Assert.NotNull(window);

        var fixes = queries.Track(window!.FirstUtc, window.LastUtc.AddSeconds(1), mmsi: null).ToList();

        Assert.Equal(harness.Count("position_report"), fixes.Count);
        Assert.Equal(fixes.OrderBy(f => f.TimestampUtc).ThenBy(f => f.Id).Select(f => f.Id), fixes.Select(f => f.Id));
        Assert.All(fixes, f => Assert.True(f.IngestRunId > 0 && f.SourceLine > 1,
            $"{harness.Name}: fix {f.Id} cites run {f.IngestRunId} line {f.SourceLine}"));

        var sample = fixes[fixes.Count / 2];
        Assert.Equal(sample.SourceLine,
            harness.Scalar($"SELECT source_line FROM position_report WHERE id = {sample.Id}"));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void TrackWindowIsHalfOpenAndTheVesselFilterHolds(Func<IStoreHarness> make)
    {
        // Bounded windows are where the SQLite timestamp-parameter skew bites: a DateTime bound as
        // "yyyy-MM-dd HH:mm:ss" against a column of "...T...Z" text returns nothing at all. These
        // windows are second-wide on purpose, the shape the skew cannot hide from.
        using var harness = make();
        Populate(harness);
        using var queries = QueriesOver(harness);

        var window = queries.FeedWindow()!;
        var all = queries.Track(window.FirstUtc, window.LastUtc.AddSeconds(1), mmsi: null).ToList();
        var t = all[all.Count / 2].TimestampUtc;

        var oneSecond = queries.Track(t, t.AddSeconds(1), mmsi: null).ToList();
        Assert.NotEmpty(oneSecond);
        Assert.All(oneSecond, f => Assert.Equal(t, f.TimestampUtc));
        Assert.Equal(all.Count(f => f.TimestampUtc == t), oneSecond.Count);

        Assert.Empty(queries.Track(t, t, mmsi: null));

        var mmsi = all[0].Mmsi;
        var oneVessel = queries.Track(window.FirstUtc, window.LastUtc.AddSeconds(1), mmsi).ToList();
        Assert.Equal(all.Count(f => f.Mmsi == mmsi), oneVessel.Count);
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void StopsAndPortCallsOverlappingAWindowAreUncapped(Func<IStoreHarness> make)
    {
        using var harness = make();
        Populate(harness);
        using var queries = QueriesOver(harness);

        var window = queries.FeedWindow()!;
        var to = window.LastUtc.AddSeconds(1);

        Assert.Equal(harness.Count("stop_event"), queries.StopsOverlapping(window.FirstUtc, to, mmsi: null).Count);
        Assert.Equal(harness.Count("port_call"), queries.PortCallsOverlapping(window.FirstUtc, to).Count);
        Assert.Empty(queries.StopsOverlapping(window.FirstUtc.AddDays(-2), window.FirstUtc.AddDays(-1), mmsi: null));

        // A stop whose last fix sits exactly on the window's opening is included: that fix is in
        // the track the replay reads, and the replay needs to know which stop it belongs to.
        var stop = queries.StopsOverlapping(window.FirstUtc, to, mmsi: null)[0];
        Assert.Contains(queries.StopsOverlapping(stop.EndedUtc, stop.EndedUtc.AddHours(1), stop.Mmsi),
            s => s.Id == stop.Id);
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void BooleanColumnsSurviveTheRoundTrip(Func<IStoreHarness> make)
    {
        // SQLite stores 0/1, Postgres true/false. A mapping that read every row as `true` would
        // silently turn every incomplete stop into a complete one -- and duration_hours is only
        // meaningful when complete (ADR-0011), so it would surface as a wrong published number
        // rather than an exception.
        using var harness = make();
        Populate(harness);
        using var queries = QueriesOver(harness);

        var stops = queries.ListStops(new StopFilter { Limit = 500 });
        Assert.NotEmpty(stops);

        // The comparison SQL has to respect the same dialect difference the test is about:
        // `is_complete = 1` is valid SQLite and a type error in Postgres.
        var trueLiteral = harness.Dialect == SqlDialect.Postgres ? "TRUE" : "1";
        var falseLiteral = harness.Dialect == SqlDialect.Postgres ? "FALSE" : "0";

        Assert.Equal(
            harness.CountWhere($"SELECT COUNT(*) FROM stop_event WHERE is_complete = {trueLiteral}"),
            stops.Count(s => s.IsComplete));
        Assert.Equal(
            harness.CountWhere($"SELECT COUNT(*) FROM stop_event WHERE status_agrees = {falseLiteral}"),
            stops.Count(s => !s.StatusAgrees));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void FiltersNarrowRatherThanSilentlyDoingNothing(Func<IStoreHarness> make)
    {
        using var harness = make();
        Populate(harness);
        using var queries = QueriesOver(harness);

        var all = queries.ListStops(new StopFilter { Limit = 500 });
        var complete = queries.ListStops(new StopFilter { CompleteOnly = true, Limit = 500 });
        var disagreeing = queries.ListStops(new StopFilter { DisagreementsOnly = true, Limit = 500 });

        Assert.All(complete, s => Assert.True(s.IsComplete));
        Assert.All(disagreeing, s => Assert.False(s.StatusAgrees));
        Assert.True(complete.Count <= all.Count);
        Assert.True(disagreeing.Count <= all.Count);
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void TheBatchedVesselLookupAgreesWithRepeatedSingleLookups(Func<IStoreHarness> make)
    {
        // The batched method is what makes DataLoader possible. If it disagreed with the
        // single-row path, the N+1 fix would change answers rather than just timings.
        using var harness = make();
        Populate(harness);
        using var queries = QueriesOver(harness);

        var mmsis = queries.ListVessels(null, 50).Select(v => v.Mmsi).ToList();
        var batched = queries.GetVessels(mmsis).OrderBy(v => v.Mmsi).ToList();
        var individually = mmsis.Select(m => queries.GetVessel(m)!).OrderBy(v => v.Mmsi).ToList();

        Assert.Equal(individually.Count, batched.Count);
        Assert.Equal(
            individually.Select(v => (v.Mmsi, v.Name, v.ShipType, v.FirstSeenUtc)),
            batched.Select(v => (v.Mmsi, v.Name, v.ShipType, v.FirstSeenUtc)));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void AskingForNothingCostsNoQuery(Func<IStoreHarness> make)
    {
        using var harness = make();
        Populate(harness);
        using var queries = QueriesOver(harness);

        var before = queries.QueryCount;
        Assert.Empty(queries.GetVessels([]));
        Assert.Empty(queries.GetPhasesForPortCalls([]));

        Assert.Equal(before, queries.QueryCount);
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void PhasesComeBackWithTheirStopsJoined(Func<IStoreHarness> make)
    {
        using var harness = make();
        Populate(harness);
        using var queries = QueriesOver(harness);

        var calls = queries.ListPortCalls(new PortCallFilter { Limit = 100 });
        Assert.NotEmpty(calls);

        var phases = queries.GetPhasesForPortCalls([.. calls.Select(c => c.Id)]);

        Assert.NotEmpty(phases);
        Assert.All(phases, p =>
        {
            Assert.Equal(p.Phase.StopId, p.Stop.Id);
            Assert.Contains(p.Phase.Phase, new[] { "Berth", "Anchorage", "Unknown" });
        });
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void RunSummariesAndTheQualityReportRoundTrip(Func<IStoreHarness> make)
    {
        using var harness = make();
        Populate(harness);
        using var queries = QueriesOver(harness);

        var run = Assert.Single(queries.ListRuns());
        Assert.True(run.RowsRead > 0);
        Assert.NotNull(run.FinishedUtc);
        Assert.True(run.FinishedUtc >= run.StartedUtc,
            $"{harness.Name}: finished before started; a timestamp mapping is wrong");

        Assert.Equal(harness.Count("quarantine"), queries.QualityReport().Sum(q => q.Quarantined));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void LimitsAreClampedRatherThanTrusted(Func<IStoreHarness> make)
    {
        // An unbounded LIMIT taken from a query string is a denial of service with extra steps.
        using var harness = make();
        Populate(harness);
        using var queries = QueriesOver(harness);

        Assert.NotEmpty(queries.ListVessels(null, 0));
        Assert.True(queries.ListVessels(null, int.MaxValue).Count <= 1_000);
    }
}
