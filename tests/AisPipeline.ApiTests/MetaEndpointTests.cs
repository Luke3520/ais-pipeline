using System.Net.Http.Json;
using AisPipeline.Core.Benchmarks;
using AisPipeline.Core.Geo;
using AisPipeline.Core.Laytime;

namespace AisPipeline.ApiTests;

/// <summary>
/// /meta projects the constants the surfaces print beside their figures.
///
/// The page states six thresholds. Copying them into JavaScript would make six captions that can
/// go stale beside a live figure, which is the defect this project exists to prevent applied to a
/// label rather than to a number (ADR-0047).
/// </summary>
public class MetaEndpointTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public MetaEndpointTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task EveryPublishedValueIsTheConstantItself()
    {
        using var client = _fixture.CreateClient();
        var meta = await client.GetFromJsonAsync<Meta>("/meta");

        Assert.NotNull(meta);
        Assert.Equal(PortAttributionThresholds.PlausiblyAtPortNm, meta.PlausiblyAtPortNm);
        Assert.Equal(BenchmarkMinimums.ForMedian, meta.BenchmarkMinimums.ForMedian);
        Assert.Equal(BenchmarkMinimums.ForPercentile, meta.BenchmarkMinimums.ForPercentile);
        Assert.Equal(CharterPartyDefaults.AllowedHours, meta.CharterPartyDefaults.AllowedHours);
        Assert.Equal(CharterPartyDefaults.RatePerDay, meta.CharterPartyDefaults.RatePerDay);
        Assert.Equal(CharterPartyDefaults.TurnHours, meta.CharterPartyDefaults.TurnHours);
        Assert.Equal(CharterPartyDefaults.Currency, meta.CharterPartyDefaults.Currency);
    }

    [Fact]
    public async Task TheDefaultsAreTheOnesTheLaytimeEndpointActuallyApplies()
    {
        // Behavioural rather than a restatement. Equality with the constants proves only that two
        // lines of C# agree; this proves the numbers a reader is shown are the ones that priced
        // the statement in front of them. A default published but not applied would be a caption
        // describing a calculation that did not happen.
        using var client = _fixture.CreateClient();

        var meta = await client.GetFromJsonAsync<Meta>("/meta");
        Assert.NotNull(meta);

        var calls = await client.GetFromJsonAsync<List<Row>>("/portcalls?limit=50");
        Assert.NotNull(calls);

        var priced = 0;

        foreach (var call in calls)
        {
            var bare = await client.GetAsync($"/portcalls/{call.Id}/laytime");

            var d = meta.CharterPartyDefaults;
            var spelled = await client.GetAsync(
                $"/portcalls/{call.Id}/laytime?allowedHours={d.AllowedHours}"
                + $"&ratePerDay={d.RatePerDay}&turnHours={d.TurnHours}&currency={d.Currency}");

            Assert.Equal(bare.StatusCode, spelled.StatusCode);
            Assert.Equal(
                await bare.Content.ReadAsStringAsync(),
                await spelled.Content.ReadAsStringAsync());

            if (bare.IsSuccessStatusCode)
            {
                priced++;
            }
        }

        // Otherwise the loop above could pass by comparing fifty identical refusals, which would
        // say nothing about the defaults reaching a calculation.
        Assert.True(priced > 0, "no call in the fixture priced, so the defaults were never applied");
    }

    private sealed record Meta(
        double PlausiblyAtPortNm,
        Minimums BenchmarkMinimums,
        Defaults CharterPartyDefaults);

    private sealed record Minimums(int ForMedian, int ForPercentile);

    private sealed record Defaults(
        double AllowedHours, double RatePerDay, double TurnHours, string Currency);

    private sealed record Row(long Id);
}
