using AisPipeline.Adapters.Csv;
using AisPipeline.Adapters.Sql;
using AisPipeline.Core.Domain;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Quality;
using AisPipeline.IntegrationTests.Harness;

namespace AisPipeline.IntegrationTests.Query;

/// <summary>
/// The per-vessel cap on batched port-call lookups, and the count that makes it detectable.
///
/// Constructed rather than taken from the fixture: the fixture spans 2h40m, so it cannot contain a
/// vessel with two port calls -- those need a twelve-hour gap or ten nautical miles of separation.
/// Writing through the store port is the honest way to build the case, and it is the same path
/// detection uses.
/// </summary>
public class PortCallCapTests
{
    private const long Mmsi = 219000999;

    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AisPipeline.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "fixtures", "sample.csv");
    }

    /// <summary>Ingests the fixture (for real position ids), then writes N port calls for one vessel.</summary>
    private static void Seed(IStoreHarness harness, int portCalls)
    {
        long anchorId;

        using (var store = harness.Create())
        {
            new IngestPipeline(store, RuleRegistry.Default(),
                new IngestOptions { ShipType = "Tanker" })
                .Run(new DmaCsvSource(FixturePath()));
        }

        using (var read = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect))
        {
            var vessel = read.ListVessels("Tanker", 1).Single();
            anchorId = read.ListFixes(vessel.Mmsi, DateTime.UnixEpoch,
                new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1).Single().Id;
        }

        var calls = new List<PortCall>();
        for (var i = 0; i < portCalls; i++)
        {
            var started = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i * 3);
            var stop = new StopEvent
            {
                Mmsi = Mmsi,
                StartedUtc = started,
                EndedUtc = started.AddHours(6),
                CentroidLatitude = 56.0 + i,
                CentroidLongitude = 10.0,
                MaxDriftNm = 0.002,
                FixCount = 100,
                ReliableFixCount = 100,
                ReportedStatus = "Moored",
                StatusAgrees = true,
                IsComplete = true,
                FirstPositionId = anchorId,
                LastPositionId = anchorId,
            };

            calls.Add(new PortCall
            {
                Mmsi = Mmsi,
                Phases = [new PortCallPhase(0, StopPhase.Berth, stop)],
            });
        }

        using var write = harness.Create();
        write.ReplaceDetections(calls);
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void PortCallsComeBackMostRecentFirst(Func<IStoreHarness> make)
    {
        // Ascending order with a Take() kept the earliest calls and discarded the newest -- the
        // opposite of what a caller asking for a vessel's port calls wants (ADR-0028).
        using var harness = make();
        Seed(harness, portCalls: 5);
        using var queries = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect);

        var calls = queries.GetPortCallsForVessels([Mmsi], limitPerVessel: 50);

        Assert.Equal(5, calls.Count);
        Assert.Equal(
            calls.Select(c => c.ArrivedUtc).OrderByDescending(a => a),
            calls.Select(c => c.ArrivedUtc));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void ACapKeepsTheNewestCallsAndTheCountStillReportsTheTruth(Func<IStoreHarness> make)
    {
        // A truncation nobody can detect is a wrong number, not a limit. The count is uncapped so
        // a client summing waiting hours can always tell its list was cut short.
        using var harness = make();
        Seed(harness, portCalls: 10);
        using var queries = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect);

        var capped = queries.GetPortCallsForVessels([Mmsi], limitPerVessel: 3);
        var total = queries.CountPortCallsForVessels([Mmsi])[Mmsi];

        Assert.Equal(3, capped.Count);
        Assert.Equal(10, total);

        // The three newest, not the three oldest.
        var newest = queries.GetPortCallsForVessels([Mmsi], limitPerVessel: 50)
            .Take(3).Select(c => c.ArrivedUtc);
        Assert.Equal(newest, capped.Select(c => c.ArrivedUtc));
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void CountingNoVesselsCostsNoQuery(Func<IStoreHarness> make)
    {
        using var harness = make();
        Seed(harness, portCalls: 1);
        using var queries = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect);

        var before = queries.QueryCount;
        Assert.Empty(queries.CountPortCallsForVessels([]));

        Assert.Equal(before, queries.QueryCount);
    }
}
