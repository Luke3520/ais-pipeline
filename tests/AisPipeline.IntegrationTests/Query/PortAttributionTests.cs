using AisPipeline.Adapters.Csv;
using AisPipeline.Adapters.Sql;
using AisPipeline.Core.Annotate;
using AisPipeline.Core.Detection;
using AisPipeline.Core.Geo;
using AisPipeline.Core.Ingest;
using AisPipeline.Core.Quality;
using AisPipeline.IntegrationTests.Harness;

namespace AisPipeline.IntegrationTests.Query;

/// <summary>
/// Port attribution through the store and back.
///
/// The name is the easy half. The distance is the half that must survive the round trip, because
/// without it the name claims the vessel was AT the port, which a point gazetteer cannot support
/// (ADR-0034).
/// </summary>
public class PortAttributionTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AisPipeline.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine([dir!.FullName, .. parts]);
    }

    /// <summary>
    /// One port, placed a short way off a port call the fixture actually produces (its centroid is
    /// 55.6745N 11.0178E). Offset deliberately rather than placed exactly on it: a zero distance
    /// would pass a test that a hardcoded 0.0 would also pass.
    /// </summary>
    private static NearestPortIndex FixtureGazetteer() => new(
    [
        new GazetteerPort
        {
            WpiNumber = 99001,
            Name = "Fixture Havn",
            Country = "Denmark",
            LatitudeDeg = 55.6800,
            LongitudeDeg = 11.0300,
        },
    ]);

    private static void Populate(IStoreHarness harness, NearestPortIndex? ports)
    {
        using (var store = harness.Create())
        {
            new IngestPipeline(store, RuleRegistry.Default(), new IngestOptions())
                .Run(new DmaCsvSource(RepoFile("fixtures", "sample.csv")));
        }

        using var detectStore = harness.Create();
        new AnnotatePass(detectStore, RuleRegistry.Default().SequenceRules).Run();
        new DetectionPass(detectStore, ports: ports).Run();
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void WithNoGazetteerEveryPortColumnIsNull(Func<IStoreHarness> make)
    {
        // The state a store built before ADR-0034 is in, and the state `detect` leaves when the
        // gazetteer file is missing. Null, not zero: zero nautical miles would read as a perfect
        // match at null island.
        using var harness = make();
        Populate(harness, ports: null);
        using var queries = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect);

        var calls = queries.ListPortCalls(new Core.Query.PortCallFilter { Limit = 500 });
        Assert.NotEmpty(calls);
        Assert.All(calls, c =>
        {
            Assert.Null(c.PortName);
            Assert.Null(c.PortDistanceNm);
            Assert.Null(c.PortWpiNumber);
            Assert.Null(c.PlausiblyAtPort);
        });
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void TheNameAndTheDistanceBothSurviveTheRoundTrip(Func<IStoreHarness> make)
    {
        using var harness = make();
        Populate(harness, FixtureGazetteer());
        using var queries = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect);

        var named = queries.ListPortCalls(new Core.Query.PortCallFilter { Limit = 500 })
            .Where(c => c.PortName is not null)
            .ToList();

        Assert.NotEmpty(named);
        Assert.All(named, c =>
        {
            Assert.Equal("Fixture Havn", c.PortName);
            Assert.Equal("Denmark", c.PortCountry);
            Assert.Equal(99001, c.PortWpiNumber);

            // A real distance, not a default. Zero would mean the centroid landed exactly on the
            // gazetteer point, which no fixture stop does.
            Assert.NotNull(c.PortDistanceNm);
            Assert.True(c.PortDistanceNm > 0, $"expected a measured distance, got {c.PortDistanceNm}");
            Assert.NotNull(c.PlausiblyAtPort);
        });
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void PlausiblyAtPortFollowsTheStoredDistanceNotAFlag(Func<IStoreHarness> make)
    {
        // The read model derives the verdict from the distance rather than storing a boolean, so
        // moving the threshold cannot leave a database disagreeing with the code that reads it.
        using var harness = make();
        Populate(harness, FixtureGazetteer());
        using var queries = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect);

        foreach (var call in queries.ListPortCalls(new Core.Query.PortCallFilter { Limit = 500 })
            .Where(c => c.PortDistanceNm is not null))
        {
            Assert.Equal(
                call.PortDistanceNm <= PortAttributionThresholds.PlausiblyAtPortNm,
                call.PlausiblyAtPort);
        }
    }

    [Theory]
    [ClassData(typeof(StoreHarnesses))]
    public void AStoreBuiltBeforeThePortColumnsGainsThemOnOpen(Func<IStoreHarness> make)
    {
        // A database created before ADR-0034 has a port_call without these columns, and
        // CREATE TABLE IF NOT EXISTS will not add them -- so every insert would fail on
        // "no such column". EnsureSchema has to notice.
        using var harness = make();

        using (var store = harness.Create())
        {
            store.EnsureSchema();
        }

        foreach (var column in new[] { "port_wpi_number", "port_name", "port_country", "port_distance_nm" })
        {
            using var connection = harness.ConnectionFactory();
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = $"ALTER TABLE port_call DROP COLUMN {column};";
            drop.ExecuteNonQuery();
        }

        // Reopening runs EnsureSchema again, which must put them back.
        Populate(harness, FixtureGazetteer());

        using var queries = new SqlAisQueries(harness.ConnectionFactory, harness.Dialect);
        var calls = queries.ListPortCalls(new Core.Query.PortCallFilter { Limit = 500 });

        Assert.NotEmpty(calls);
        Assert.Contains(calls, c => c.PortName == "Fixture Havn");
    }
}
