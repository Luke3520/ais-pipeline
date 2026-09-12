using AisPipeline.Adapters.Csv;
using AisPipeline.Adapters.Sql;
using AisPipeline.Core.Domain;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Quality;
using AisPipeline.Core.Query;
using AisPipeline.IntegrationTests.Harness;

namespace AisPipeline.IntegrationTests.Query;

/// <summary>
/// "The most recent complete port call" is its own query, not a sort over a page of
/// <see cref="SqlAisQueries.ListPortCalls"/>.
///
/// That page orders by total duration before applying its limit, so once a vessel has more calls
/// than the page holds, the genuinely most recent one can be absent entirely -- and a caller
/// sorting what survived would price demurrage against a stale, longer call while reporting it as
/// current.
/// </summary>
public class MostRecentPortCallTests
{
    private const long Mmsi = 219000997;

    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AisPipeline.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "fixtures", "sample.csv");
    }

    /// <summary>Many long historical calls, then one short recent one.</summary>
    private static void Seed(IStoreHarness harness, int historical)
    {
        long anchorId;
        long mmsi;

        using (var store = harness.Create())
        {
            new IngestPipeline(store, RuleRegistry.Default(),
                new IngestOptions { ShipType = "Tanker" })
                .Run(new DmaCsvSource(FixturePath()));
        }

        using (var read = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect))
        {
            mmsi = read.ListVessels("Tanker", 1).Single().Mmsi;
        }

        // Any real position id, so the synthetic stop below points at a row that exists. Read
        // straight from the table rather than through a query method: these tests need an id, not
        // a window, and the read side should not carry a method whose only caller is a fixture.
        anchorId = harness.Scalar($"SELECT MIN(id) FROM position_report WHERE mmsi = {mmsi}");

        {
        }

        StopEvent Stop(DateTime start, double hours) => new()
        {
            Mmsi = Mmsi,
            StartedUtc = start,
            EndedUtc = start.AddHours(hours),
            CentroidLatitude = 56.0,
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

        var calls = new List<PortCall>();
        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < historical; i++)
        {
            calls.Add(new PortCall
            {
                Mmsi = Mmsi,
                // Long: 80 hours each, so they dominate a duration-ranked page.
                Phases = [new PortCallPhase(0, StopPhase.Berth, Stop(epoch.AddDays(i * 2), 80))],
            });
        }

        calls.Add(new PortCall
        {
            Mmsi = Mmsi,
            // The most recent call, and the shortest -- exactly the one a duration-ranked page drops.
            Phases = [new PortCallPhase(0, StopPhase.Berth, Stop(epoch.AddDays(historical * 2 + 10), 3))],
        });

        using var write = harness.Create();
        write.ReplaceDetections(calls);
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void TheMostRecentCallIsFoundEvenWhenItIsTheShortest(Func<IStoreHarness> make)
    {
        using var harness = make();
        Seed(harness, historical: 60);
        using var queries = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect);

        var mostRecent = queries.MostRecentCompletePortCall(Mmsi);

        Assert.NotNull(mostRecent);
        Assert.Equal(
            queries.ListPortCalls(new PortCallFilter { Mmsi = Mmsi, Limit = 1000 })
                .Max(c => c.ArrivedUtc),
            mostRecent!.ArrivedUtc);
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void SortingADurationRankedPageWouldHavePickedTheWrongCall(Func<IStoreHarness> make)
    {
        // The defect this query exists to avoid, demonstrated rather than described.
        using var harness = make();
        Seed(harness, historical: 60);
        using var queries = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect);

        var viaPage = queries
            .ListPortCalls(new PortCallFilter { Mmsi = Mmsi, CompleteOnly = true, Limit = 50 })
            .OrderByDescending(c => c.ArrivedUtc)
            .First();

        var correct = queries.MostRecentCompletePortCall(Mmsi)!;

        Assert.NotEqual(correct.ArrivedUtc, viaPage.ArrivedUtc);
        Assert.True(correct.ArrivedUtc > viaPage.ArrivedUtc,
            $"{harness.Name}: the dedicated query should find a later call than the page does");
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void AVesselWithNoCallsReturnsNullRatherThanThrowing(Func<IStoreHarness> make)
    {
        using var harness = make();
        Seed(harness, historical: 1);
        using var queries = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect);

        Assert.Null(queries.MostRecentCompletePortCall(999_999_999));
    }
}
