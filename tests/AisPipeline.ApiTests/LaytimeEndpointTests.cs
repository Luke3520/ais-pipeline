using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AisPipeline.ApiTests;

/// <summary>
/// The endpoint that carries a money figure.
///
/// Held to a higher bar than the descriptive ones: a demurrage total is an artefact someone argues
/// with, so the response has to decompose into line items, say what it assumed, and refuse legibly
/// when the evidence will not support a figure (ADR-0042).
/// </summary>
public class LaytimeEndpointTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public LaytimeEndpointTests(ApiFixture fixture) => _fixture = fixture;

    private async Task<(HttpStatusCode Status, JsonElement Body)> Get(string url)
    {
        using var client = _fixture.CreateClient();
        var response = await client.GetAsync(url);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, body);
    }

    /// <summary>
    /// A call from the fixture that actually prices.
    ///
    /// Asserts rather than returning null. An earlier version let the tests below return early
    /// when nothing was priceable, which meant they passed while testing nothing -- the same
    /// failure AdapterCoverageTests exists to prevent: a check that skips looks exactly like a
    /// check that passed. The fixture yields two berth calls, so if this ever finds none, that is
    /// the finding.
    /// </summary>
    private async Task<long> PriceableCallId()
    {
        using var client = _fixture.CreateClient();
        var calls = await client.GetFromJsonAsync<JsonElement>("/portcalls?limit=200");

        foreach (var call in calls.EnumerateArray())
        {
            var id = call.GetProperty("id").GetInt64();
            var (status, _) = await Get($"/portcalls/{id}/laytime");
            if (status == HttpStatusCode.OK)
            {
                return id;
            }
        }

        Assert.Fail("no port call in the fixture could be priced; the laytime tests below would "
            + "have silently tested nothing");
        return 0;
    }

    [Fact]
    public async Task AnUnknownPortCallIsNotFound()
    {
        var (status, body) = await Get("/portcalls/999999/laytime");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("999999", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ACallThatCannotBePricedRefusesLegiblyRatherThanFailing()
    {
        // 422, not 400 and not 500: the request is well formed and the call exists. What is missing
        // is evidence, and the body has to name which -- a refusal nobody can read is
        // indistinguishable from a server fault.
        using var client = _fixture.CreateClient();
        var calls = await client.GetFromJsonAsync<JsonElement>("/portcalls?limit=200");

        var refusals = new List<JsonElement>();
        foreach (var call in calls.EnumerateArray())
        {
            var (status, body) = await Get($"/portcalls/{call.GetProperty("id").GetInt64()}/laytime");
            if (status == HttpStatusCode.UnprocessableEntity)
            {
                refusals.Add(body);
            }
        }

        // The fixture is a two-hour slice, so most calls are anchorage-only and cannot be priced.
        Assert.NotEmpty(refusals);
        foreach (var refusal in refusals)
        {
            Assert.False(refusal.GetProperty("priced").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(refusal.GetProperty("refusal").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(refusal.GetProperty("detail").GetString()));
        }
    }

    [Fact]
    public async Task NegativeTermsAreRejected()
    {
        var (status, _) = await Get("/portcalls/1/laytime?allowedHours=-5");
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task APricedCallDecomposesIntoLinesThatAccountForEveryHour()
    {
        var id = await PriceableCallId();

        var (status, body) = await Get($"/portcalls/{id}/laytime?allowedHours=12&ratePerDay=24000");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("priced").GetBoolean());

        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.NotEmpty(lines);

        // The accounting identity: every hour between commencement and completion belongs to
        // exactly one line. A total nobody can decompose is a total nobody can dispute.
        var commenced = body.GetProperty("commencedUtc").GetDateTime();
        var completed = body.GetProperty("completedUtc").GetDateTime();
        var covered = lines.Sum(l => l.GetProperty("hours").GetDouble());
        var span = (completed - commenced).TotalHours;

        var beforeCommencement = lines
            .Where(l => l.GetProperty("kind").GetString() == "BeforeCommencement")
            .Sum(l => l.GetProperty("hours").GetDouble());

        Assert.Equal(span, covered - beforeCommencement, 3);
    }

    [Fact]
    public async Task AnAssumedNoticeOfReadinessSaysSoOnTheFigureItProduced()
    {
        var id = await PriceableCallId();

        // AIS cannot observe a notice of readiness, so one has to be supplied or assumed. The
        // assumption travels with the figure rather than living in documentation (ADR-0030).
        var (_, assumed) = await Get($"/portcalls/{id}/laytime");
        Assert.True(assumed.GetProperty("noticeOfReadinessIsAssumed").GetBoolean());

        var arrival = assumed.GetProperty("arrivedUtc").GetDateTime();
        Assert.Equal(arrival, assumed.GetProperty("noticeOfReadinessUtc").GetDateTime());

        var given = arrival.AddHours(2).ToString("O");
        var (_, supplied) = await Get($"/portcalls/{id}/laytime?norUtc={Uri.EscapeDataString(given)}");
        Assert.False(supplied.GetProperty("noticeOfReadinessIsAssumed").GetBoolean());
    }

    [Fact]
    public async Task AShorterAllowanceCanOnlyIncreaseDemurrage()
    {
        var id = await PriceableCallId();

        var (_, generous) = await Get($"/portcalls/{id}/laytime?allowedHours=500&ratePerDay=24000");
        var (_, tight) = await Get($"/portcalls/{id}/laytime?allowedHours=1&ratePerDay=24000");

        Assert.True(
            tight.GetProperty("demurrageHours").GetDouble()
            >= generous.GetProperty("demurrageHours").GetDouble());
        Assert.Equal(0.0, generous.GetProperty("demurrageHours").GetDouble(), 3);
    }
}
