using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AisPipeline.ApiTests;

/// <summary>
/// Benchmarks over HTTP. The figure is easy; the sample it rests on is the contract (ADR-0043).
/// </summary>
public class PortBenchmarkEndpointTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public PortBenchmarkEndpointTests(ApiFixture fixture) => _fixture = fixture;

    private async Task<JsonElement> Get(string url)
    {
        using var client = _fixture.CreateClient();
        return await client.GetFromJsonAsync<JsonElement>(url);
    }

    [Fact]
    public async Task EveryPortAccountsForEveryCallAttributedToIt()
    {
        // The identity that makes the exclusions checkable: usable + incomplete + too far equals
        // attributed, for every port. A benchmark that quietly drops calls is unauditable.
        var ports = await Get("/ports");
        Assert.NotEqual(0, ports.GetArrayLength());

        foreach (var port in ports.EnumerateArray())
        {
            var attributed = port.GetProperty("attributedCalls").GetInt32();
            var usable = port.GetProperty("usableCalls").GetInt32();
            var incomplete = port.GetProperty("excludedIncomplete").GetInt32();
            var tooFar = port.GetProperty("excludedTooFar").GetInt32();

            Assert.Equal(attributed, usable + incomplete + tooFar);
        }
    }

    [Fact]
    public async Task AThinSampleGetsNoMedianRatherThanAThinOne()
    {
        var ports = await Get("/ports");

        foreach (var port in ports.EnumerateArray())
        {
            var usable = port.GetProperty("usableCalls").GetInt32();
            var median = port.GetProperty("medianWaitingHours");
            var p90 = port.GetProperty("p90WaitingHours");

            // Null below the minimum, a number at or above it. Never a confident-looking zero.
            Assert.Equal(usable < 5, median.ValueKind == JsonValueKind.Null);
            Assert.Equal(usable < 10, p90.ValueKind == JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task AnUnknownPortIsNotFound()
    {
        using var client = _fixture.CreateClient();
        var response = await client.GetAsync("/ports/99999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AWaitIsRankedAgainstTheCallsThatActuallyHappened()
    {
        var ports = await Get("/ports");
        var biggest = ports.EnumerateArray()
            .OrderByDescending(p => p.GetProperty("usableCalls").GetInt32())
            .First();

        var wpi = biggest.GetProperty("wpiNumber").GetInt32();
        var observed = biggest.GetProperty("usableCalls").GetInt32();

        var ranked = await Get($"/ports/{wpi}?waitingHours=1000000");
        var comparison = ranked.GetProperty("comparison");

        // A wait longer than every observed call ranks at the top, against the usable sample.
        Assert.Equal(observed, comparison.GetProperty("ofCalls").GetInt32());

        if (observed >= 5)
        {
            Assert.Equal(observed, comparison.GetProperty("longerThanCalls").GetInt32());
            Assert.Equal(100.0, comparison.GetProperty("percentile").GetDouble(), 1);
        }
        else
        {
            // Too thin to rank against: a position, not a zero.
            Assert.Equal(JsonValueKind.Null, comparison.GetProperty("percentile").ValueKind);
        }
    }

    [Fact]
    public async Task TheObservationsArePublishedSoThePercentilesCanBeChecked()
    {
        var ports = await Get("/ports");
        var wpi = ports.EnumerateArray()
            .OrderByDescending(p => p.GetProperty("usableCalls").GetInt32())
            .First().GetProperty("wpiNumber").GetInt32();

        var port = await Get($"/ports/{wpi}");
        var observed = port.GetProperty("waitingHoursObserved").EnumerateArray()
            .Select(h => h.GetDouble()).ToList();

        Assert.Equal(port.GetProperty("usableCalls").GetInt32(), observed.Count);
        Assert.Equal(observed.Order().ToList(), observed);

        // A nearest-rank percentile is always one of the observations, never interpolated.
        if (port.GetProperty("medianWaitingHours").ValueKind != JsonValueKind.Null)
        {
            Assert.Contains(port.GetProperty("medianWaitingHours").GetDouble(), observed);
        }
    }

    [Fact]
    public async Task AskingForNoComparisonReturnsNone()
    {
        var ports = await Get("/ports");
        var wpi = ports.EnumerateArray().First().GetProperty("wpiNumber").GetInt32();

        var port = await Get($"/ports/{wpi}");
        Assert.Equal(JsonValueKind.Null, port.GetProperty("comparison").ValueKind);
    }
}
