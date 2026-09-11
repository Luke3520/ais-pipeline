using System.Net.Http.Json;
using System.Text.Json;
using AisPipeline.Adapters.Sql;
using AisPipeline.Core.Ports;
using AisPipeline.Core.Query;
using Microsoft.Extensions.DependencyInjection;

namespace AisPipeline.ApiTests;

/// <summary>
/// Proves the N+1 is gone by counting queries.
///
/// This is the only honest way to assert it. A GraphQL response looks identical whether it took
/// one round trip or a hundred, and wall-clock timing over a small fixture does not separate them
/// reliably — a hundred queries against a warm local SQLite file is still milliseconds. Having
/// DataLoader wired up is not evidence that it is being used; the count is (ADR-0015).
/// </summary>
public class GraphQLNPlusOneTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public GraphQLNPlusOneTests(ApiFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Runs a query with a query-counting <see cref="IAisQueries"/> scoped to that one request,
    /// and returns both the data and how many times the database was actually asked.
    /// </summary>
    private async Task<(JsonElement Data, int Queries)> Execute(string query)
    {
        // A shared tally rather than a captured instance. HotChocolate resolves services from
        // more than one scope during an execution -- the query root from the request scope, the
        // DataLoaders from their own -- so counting a single instance's calls silently measures
        // part of the request and reports a plausible, smaller number.
        var tally = new QueryTally();

        using var factory = _fixture.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.AddScoped<IAisQueries>(sp =>
            {
                var options = sp.GetRequiredService<AisPipeline.Api.StoreOptions>();
                return new CountingQueries(
                    new SqlAisQueries(options.ConnectionFactory, options.Dialect), tally);
            });
        }));

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/graphql", new { query });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        if (body.TryGetProperty("errors", out var errors))
        {
            Assert.Fail($"GraphQL errors: {errors}");
        }

        return (body.GetProperty("data"), tally.Total);
    }

    [Fact]
    public async Task ResolvingTheVesselOfEveryPortCallCostsOneQueryNotOnePerCall()
    {
        // The textbook case. Without DataLoader this is one lookup per port call.
        var (data, queries) = await Execute("""
            { portCalls(limit: 50) { id mmsi waitingHours workingHours vessel { mmsi name shipType } } }
            """);

        var calls = data.GetProperty("portCalls");
        Assert.True(calls.GetArrayLength() > 1,
            "the fixture no longer yields several port calls; this test proves nothing");

        // One query for the port calls, one batched query for every vessel they refer to.
        Assert.Equal(2, queries);

        foreach (var call in calls.EnumerateArray())
        {
            Assert.Equal(call.GetProperty("mmsi").GetInt64(),
                call.GetProperty("vessel").GetProperty("mmsi").GetInt64());
        }
    }

    [Fact]
    public async Task QueryCountDoesNotGrowWithThePageSize()
    {
        // The property that distinguishes batched from unbatched: asking for more rows must cost
        // the same number of round trips, not more.
        // Limits chosen so the fixture actually returns different row counts: it holds a handful
        // of port calls, so 5 versus 50 would return the same rows and prove nothing.
        var (small, smallQueries) = await Execute("{ portCalls(limit: 1) { vessel { name } } }");
        var (large, largeQueries) = await Execute("{ portCalls(limit: 50) { vessel { name } } }");

        Assert.True(large.GetProperty("portCalls").GetArrayLength()
            > small.GetProperty("portCalls").GetArrayLength());
        Assert.Equal(smallQueries, largeQueries);
    }

    [Fact]
    public async Task TheFullyNestedAnalyticalQueryStaysFlatInQueryCount()
    {
        // vessel -> port calls -> phases -> stop. Unbatched this is one query for the vessels,
        // one per vessel for its calls, and one per call for its phases: with 20 vessels and a
        // handful of calls each, well over a hundred round trips.
        var (data, queries) = await Execute("""
            {
              vessels(shipType: "Tanker", limit: 20) {
                mmsi
                name
                portCalls {
                  id
                  waitingHours
                  workingHours
                  unclassifiedHours
                  phases { phase { sequence phase } stop { durationHours maxDriftNm isComplete statusAgrees } }
                }
              }
            }
            """);

        Assert.NotEqual(0, data.GetProperty("vessels").GetArrayLength());

        // vessels + port calls + phases-with-stops = three, however deep the nesting goes.
        Assert.True(queries <= 3, $"expected at most 3 queries for the nested read, took {queries}");
    }

    [Fact]
    public async Task AnExcessivelyDeepQueryIsRefused()
    {
        // Each level multiplies the work, so unbounded depth is a denial of service. The limit is
        // 10; this nests past it.
        using var client = _fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/graphql", new
        {
            query = """
                { vessels { portCalls { vessel { portCalls { vessel { portCalls {
                  vessel { portCalls { vessel { portCalls { id } } } } } } } } } } }
                """,
        });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("errors", out _), "a query past the depth limit was accepted");
    }

    [Fact]
    public async Task TheGraphSurfacesTheProjectsHeadlineFinding()
    {
        // The disagreement between a vessel's own status and its speed is the point of the
        // project, so it has to be answerable in one query rather than reconstructed by a client.
        var (data, _) = await Execute("""
            { stops(disagreementsOnly: true, limit: 25) { mmsi durationHours reportedStatus statusAgrees } }
            """);

        var stops = data.GetProperty("stops");
        Assert.NotEqual(0, stops.GetArrayLength());
        Assert.Contains(stops.EnumerateArray(),
            s => s.GetProperty("reportedStatus").GetString()!.StartsWith("Under way", StringComparison.Ordinal));
        foreach (var stop in stops.EnumerateArray())
        {
            Assert.False(stop.GetProperty("statusAgrees").GetBoolean());
        }
    }

    /// <summary>Queries issued across every scope of one execution.</summary>
    private sealed class QueryTally
    {
        private readonly List<SqlAisQueries> _instances = [];

        public void Track(SqlAisQueries instance) => _instances.Add(instance);

        public int Total => _instances.Sum(i => i.QueryCount);
    }

    /// <summary>Counts how many times the database was asked, delegating everything else.</summary>
    private sealed class CountingQueries : IAisQueries
    {
        private readonly SqlAisQueries _inner;

        public CountingQueries(SqlAisQueries inner, QueryTally tally)
        {
            _inner = inner;
            tally.Track(inner);
        }

        public StoredVessel? GetVessel(long mmsi) => _inner.GetVessel(mmsi);

        public IReadOnlyList<StoredVessel> GetVessels(IReadOnlyCollection<long> mmsis) =>
            _inner.GetVessels(mmsis);

        public IReadOnlyList<StoredVessel> ListVessels(string? shipType, int limit) =>
            _inner.ListVessels(shipType, limit);

        public IReadOnlyList<StoredStop> ListStops(StopFilter filter) => _inner.ListStops(filter);

        public IReadOnlyList<StoredStop> GetStopsForVessels(
            IReadOnlyCollection<long> mmsis, int limitPerVessel) =>
            _inner.GetStopsForVessels(mmsis, limitPerVessel);

        public IReadOnlyList<StoredPortCall> ListPortCalls(PortCallFilter filter) =>
            _inner.ListPortCalls(filter);

        public IReadOnlyList<StoredPortCall> GetPortCallsForVessels(
            IReadOnlyCollection<long> mmsis, int limitPerVessel) =>
            _inner.GetPortCallsForVessels(mmsis, limitPerVessel);

        public IReadOnlyList<(StoredPhase Phase, StoredStop Stop)> GetPhasesForPortCalls(
            IReadOnlyCollection<long> portCallIds) =>
            _inner.GetPhasesForPortCalls(portCallIds);

        public IReadOnlyList<AisPipeline.Core.Domain.PositionFix> ListFixes(
            long mmsi, DateTime fromUtc, DateTime toUtc, int limit) =>
            _inner.ListFixes(mmsi, fromUtc, toUtc, limit);

        public IReadOnlyList<RuleHitCount> QualityReport() => _inner.QualityReport();

        public IReadOnlyList<StoredRun> ListRuns() => _inner.ListRuns();

        public void Dispose() => _inner.Dispose();
    }
}
