using System.Net;
using System.Text.RegularExpressions;

namespace AisPipeline.ApiTests;

/// <summary>
/// The showcase page is served, and everything it fetches answers.
///
/// ADR-0037 deferred Playwright and named what replaces it: the page is read-only tables over
/// endpoints the contract suite already covers, so a browser test would add coverage of rendering,
/// not of behaviour. What is NOT covered elsewhere is the wiring -- that the static files are
/// served at all, and that the URLs the script fetches still exist. Both are silent failures: a
/// missing wwwroot gives a 404 page nobody sees in CI, and a renamed endpoint leaves a table
/// reading "loading…" forever.
/// </summary>
public class ShowcasePageTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public ShowcasePageTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task TheRootServesThePage()
    {
        using var client = _fixture.CreateClient();
        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("ais-pipeline", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/styles.css", "text/css")]
    [InlineData("/app.js", "text/javascript")]
    public async Task TheAssetsThePageReferencesAreServed(string path, string mediaType)
    {
        using var client = _fixture.CreateClient();
        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task EveryEndpointTheScriptFetchesAnswers()
    {
        // Read out of app.js rather than listed here. A list would be a second place to update, and
        // the failure it guards against is exactly someone changing one and not the other.
        var script = await ReadAsset("/app.js");

        var urls = Regex.Matches(script, @"json\('([^']+)'\)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(urls);

        using var client = _fixture.CreateClient();
        foreach (var url in urls)
        {
            var response = await client.GetAsync(url);
            Assert.True(
                response.IsSuccessStatusCode,
                $"the page fetches {url}, which returned {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task ThePageDoesNotReachForPositionsOrAWriteSurface()
    {
        // ADR-0037's two scope limits, asserted rather than trusted. Positions are the read-path
        // problem ADR-0029 warns about, and any write would cross its second trigger and make
        // authentication non-deferrable (ADR-0017).
        var script = await ReadAsset("/app.js");

        Assert.DoesNotContain("/fixes", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("position_report", script, StringComparison.OrdinalIgnoreCase);

        foreach (var verb in new[] { "POST", "PUT", "PATCH", "DELETE" })
        {
            Assert.DoesNotContain($"'{verb}'", script, StringComparison.Ordinal);
        }
    }

    private async Task<string> ReadAsset(string path)
    {
        using var client = _fixture.CreateClient();
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
