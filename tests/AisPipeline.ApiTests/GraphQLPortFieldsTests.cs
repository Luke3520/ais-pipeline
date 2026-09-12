using System.Net.Http.Json;
using System.Text.Json;
using AisPipeline.Core.Geo;

namespace AisPipeline.ApiTests;

/// <summary>
/// The port attribution over GraphQL.
///
/// The fields reached this surface for free — they are properties on the read model HotChocolate
/// already projects — which is exactly why they needed a test. Nothing asserted that a heuristic
/// and a nullable distance survive the schema, and "it compiles" is not evidence that a client
/// sees a number rather than a zero.
/// </summary>
public class GraphQLPortFieldsTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public GraphQLPortFieldsTests(ApiFixture fixture) => _fixture = fixture;

    private async Task<JsonElement> Query(string query)
    {
        using var client = _fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/graphql", new { query });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(
            body.TryGetProperty("errors", out var errors),
            $"GraphQL returned errors: {errors}");

        return body.GetProperty("data");
    }

    [Fact]
    public async Task ThePortFieldsResolveAndCarryTheirDistance()
    {
        var data = await Query("""
            { portCalls(limit: 50) {
                id portName portCountry portWpiNumber portDistanceNm plausiblyAtPort
            } }
            """);

        var calls = data.GetProperty("portCalls").EnumerateArray().ToList();
        Assert.NotEmpty(calls);

        var named = calls
            .Where(c => c.GetProperty("portName").ValueKind != JsonValueKind.Null)
            .ToList();

        Assert.NotEmpty(named);
        foreach (var call in named)
        {
            Assert.Equal(ApiFixture.GazetteerPortName, call.GetProperty("portName").GetString());
            Assert.Equal(99001, call.GetProperty("portWpiNumber").GetInt32());

            // The distance is the half that stops the name overclaiming, so it must be present and
            // real wherever a name is (ADR-0034).
            Assert.Equal(JsonValueKind.Number, call.GetProperty("portDistanceNm").ValueKind);
            Assert.True(call.GetProperty("portDistanceNm").GetDouble() > 0);
        }
    }

    [Fact]
    public async Task AnUnnamedPortIsNullAcrossEveryFieldOverGraphQLToo()
    {
        var data = await Query("""
            { portCalls(limit: 50) {
                portName portCountry portWpiNumber portDistanceNm plausiblyAtPort
            } }
            """);

        var unnamed = data.GetProperty("portCalls").EnumerateArray()
            .Where(c => c.GetProperty("portName").ValueKind == JsonValueKind.Null)
            .ToList();

        Assert.NotEmpty(unnamed);
        foreach (var call in unnamed)
        {
            // Nullable all the way through the schema. A non-null Float here would make GraphQL
            // coerce a missing distance to 0.0 -- a berth at the reference point, which is the most
            // confident possible wrong answer.
            Assert.Equal(JsonValueKind.Null, call.GetProperty("portDistanceNm").ValueKind);
            Assert.Equal(JsonValueKind.Null, call.GetProperty("plausiblyAtPort").ValueKind);
            Assert.Equal(JsonValueKind.Null, call.GetProperty("portWpiNumber").ValueKind);
            Assert.Equal(JsonValueKind.Null, call.GetProperty("portCountry").ValueKind);
        }
    }

    [Fact]
    public async Task PlausiblyAtPortMatchesTheThresholdAppliedToTheDistance()
    {
        var data = await Query("{ portCalls(limit: 50) { portDistanceNm plausiblyAtPort } }");

        foreach (var call in data.GetProperty("portCalls").EnumerateArray())
        {
            if (call.GetProperty("portDistanceNm").ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            Assert.Equal(
                call.GetProperty("portDistanceNm").GetDouble()
                    <= PortAttributionThresholds.PlausiblyAtPortNm,
                call.GetProperty("plausiblyAtPort").GetBoolean());
        }
    }
}
