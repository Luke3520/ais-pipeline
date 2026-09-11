using System.Net;

using System.Net.Http.Json;
using System.Text.Json;

namespace AisPipeline.ApiTests;

/// <summary>
/// The REST surface, over a real database built from the committed fixture.
///
/// Asserts on content rather than status codes alone. A 200 carrying an empty array looks
/// identical to a working endpoint, and every one of these can return an empty array legitimately,
/// so "it responded" proves nothing on its own.
/// </summary>
public class RestContractTests : IClassFixture<ApiFixture>
{
    private readonly HttpClient _client;

    public RestContractTests(ApiFixture fixture) => _client = fixture.CreateClient();

    private async Task<JsonElement> GetJson(string url)
    {
        var response = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task HealthReportsOk()
    {
        var body = await GetJson("/health");

        Assert.Equal("ok", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task VesselsReturnsTankersWithTheirIdentityResolved()
    {
        var vessels = await GetJson("/vessels?shipType=Tanker&limit=50");

        Assert.NotEqual(0, vessels.GetArrayLength());
        foreach (var v in vessels.EnumerateArray())
        {
            Assert.Equal("Tanker", v.GetProperty("shipType").GetString());
            Assert.True(v.GetProperty("mmsi").GetInt64() > 0);
        }
    }

    [Fact]
    public async Task AnUnknownVesselIsNotFoundRatherThanAnEmptyBody()
    {
        var response = await _client.GetAsync("/vessels/1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TimestampsAreSerialisedAsUtc()
    {
        // The pipeline is UTC end to end. A serialiser that dropped the offset would make every
        // consumer guess, and a stop boundary read two hours out is a wrong laytime figure.
        var vessels = await GetJson("/vessels?limit=1");
        var firstSeen = vessels[0].GetProperty("firstSeenUtc").GetString()!;

        Assert.EndsWith("Z", firstSeen, StringComparison.Ordinal);
        Assert.Equal(DateTimeKind.Utc, DateTime.Parse(firstSeen,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal |
            System.Globalization.DateTimeStyles.AssumeUniversal).Kind);
    }

    [Fact]
    public async Task StopsCanBeNarrowedToCompleteOnes()
    {
        var all = await GetJson("/stops?limit=200");
        var complete = await GetJson("/stops?limit=200&completeOnly=true");

        Assert.True(complete.GetArrayLength() <= all.GetArrayLength());
        foreach (var s in complete.EnumerateArray())
        {
            Assert.True(s.GetProperty("isComplete").GetBoolean());
        }
    }

    [Fact]
    public async Task StopsCanBeNarrowedToTheRuleTenDisagreements()
    {
        var disagreeing = await GetJson("/stops?limit=200&disagreementsOnly=true");

        foreach (var s in disagreeing.EnumerateArray())
        {
            Assert.False(s.GetProperty("statusAgrees").GetBoolean());
        }
    }

    [Fact]
    public async Task PortCallsCarryTheWaitingWorkingSplit()
    {
        var calls = await GetJson("/portcalls?limit=50");

        Assert.NotEqual(0, calls.GetArrayLength());
        foreach (var c in calls.EnumerateArray())
        {
            // Unclassified hours are surfaced rather than folded into either figure (ADR-0025).
            Assert.True(c.GetProperty("waitingHours").GetDouble() >= 0);
            Assert.True(c.GetProperty("workingHours").GetDouble() >= 0);
            Assert.True(c.GetProperty("unclassifiedHours").GetDouble() >= 0);
        }
    }

    [Fact]
    public async Task QualityReportsWhatThePipelineRefused()
    {
        var quality = await GetJson("/quality");

        Assert.NotEqual(0, quality.GetArrayLength());
        foreach (var rule in quality.EnumerateArray())
        {
            Assert.StartsWith("R", rule.GetProperty("ruleId").GetString(), StringComparison.Ordinal);
            Assert.True(rule.GetProperty("quarantined").GetInt64() > 0);
        }
    }

    [Fact]
    public async Task TheApiServesTheDatabaseThisFixtureBuiltAndNoOther()
    {
        // The fixture ingests exactly once, so exactly one run must be visible. Any other count
        // means the host resolved a different database -- which is what happened before
        // ADR-0028's fix: an ambient AIS_POSTGRES took precedence and these tests asserted
        // against a populated Postgres they never wrote to, passing locally and failing in CI
        // only because CI's Postgres was empty.
        var runs = await GetJson("/runs");

        Assert.Equal(1, runs.GetArrayLength());
        Assert.EndsWith("sample.csv", runs[0].GetProperty("sourceFile").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunsExposeBothDuplicateCountersSeparately()
    {
        // They are separate deliberately: 38% of a file is duplicate on first ingest, so one
        // combined counter would make the idempotency proof unreadable (ADR-0012).
        var runs = await GetJson("/runs");

        var run = runs[0];
        Assert.True(run.GetProperty("rowsRead").GetInt64() > 0);
        Assert.True(run.TryGetProperty("rowsDuplicateInFile", out _));
        Assert.True(run.TryGetProperty("rowsDuplicatePriorRun", out _));
    }

    [Fact]
    public async Task OpenApiDocumentIsServedAndDescribesTheEndpoints()
    {
        var document = await GetJson("/openapi/v1.json");
        var paths = document.GetProperty("paths");

        foreach (var expected in new[] { "/vessels", "/stops", "/portcalls", "/quality", "/runs", "/health" })
        {
            Assert.True(paths.TryGetProperty(expected, out _), $"{expected} missing from the OpenAPI document");
        }
    }

    [Fact]
    public async Task AnOversizedLimitIsClampedRatherThanHonoured()
    {
        var response = await _client.GetAsync("/vessels?limit=999999");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var vessels = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(vessels.GetArrayLength() <= 1000);
    }
}
