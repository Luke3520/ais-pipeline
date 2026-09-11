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
/// DataLoader wired up is not evidence that it is being used; the count is (ADR-0027).
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
                return tally.Track(new SqlAisQueries(options.ConnectionFactory, options.Dialect));
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
    public async Task ACappedPortCallListIsDetectableRatherThanSilent()
    {
        // A truncation nobody can detect is a wrong number, not a limit. portCallCount is
        // uncapped, so a client can always tell a vessel that made exactly the cap's worth of
        // calls from one whose list was cut short (ADR-0028).
        var (data, _) = await Execute(
            "{ vessels(limit: 20) { mmsi portCallCount portCalls { id arrivedUtc } } }");

        var vessels = data.GetProperty("vessels");
        Assert.NotEqual(0, vessels.GetArrayLength());

        foreach (var vessel in vessels.EnumerateArray())
        {
            var returned = vessel.GetProperty("portCalls").GetArrayLength();
            var total = vessel.GetProperty("portCallCount").GetInt64();

            Assert.True(total >= returned,
                $"mmsi {vessel.GetProperty("mmsi").GetInt64()}: count {total} below the {returned} returned");
        }
    }

    [Fact]
    public async Task TheUncappedCountCostsNoExtraQueryPerVessel()
    {
        // The count is itself batched, so adding it does not reintroduce the N+1 it exists to
        // make visible.
        var (_, withoutCount) = await Execute("{ vessels(limit: 20) { portCalls { id } } }");
        var (_, withCount) = await Execute("{ vessels(limit: 20) { portCallCount portCalls { id } } }");

        Assert.Equal(withoutCount + 1, withCount);
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

    /// <summary>
    /// Queries issued across every scope of one execution.
    ///
    /// A tally over instances rather than a decorator: SqlAisQueries already implements
    /// IAisQueries, so a wrapper would be eleven pass-through methods with one call site, and
    /// every future port method would have to be hand-forwarded for no behavioural difference.
    /// </summary>
    private sealed class QueryTally
    {
        private readonly List<SqlAisQueries> _instances = [];

        public SqlAisQueries Track(SqlAisQueries instance)
        {
            _instances.Add(instance);
            return instance;
        }

        /// <summary>
        /// Summed across scopes. HotChocolate resolves the query root and the DataLoaders from
        /// different scopes, so counting one instance measures part of a request and reports a
        /// smaller, plausible number.
        /// </summary>
        public int Total => _instances.Sum(i => i.QueryCount);
    }
}
