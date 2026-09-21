using System.Net.Http.Json;
using System.Net;
using System.Text.RegularExpressions;

namespace AisPipeline.ApiTests;

/// <summary>
/// The page is served, and everything it fetches answers.
///
/// ADR-0037 deferred Playwright and named what replaces it: the page is read-only views over
/// endpoints the contract suite already covers, so a browser test would add coverage of rendering,
/// not of behaviour. ADR-0047 widened the page and kept that deferral, because what the new views
/// add is a form that builds a URL -- and a URL is assertable from here.
///
/// What is NOT covered elsewhere is the wiring: that the static files are served at all, and that
/// the URLs the script fetches still exist. Both are silent failures -- a missing wwwroot gives a
/// 404 page nobody sees in CI, and a renamed endpoint leaves a table reading "loading…" forever.
/// </summary>
public class ShowcasePageTests : IClassFixture<ApiFixture>
{
    private readonly ApiFixture _fixture;

    public ShowcasePageTests(ApiFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The parameterised URLs the script is allowed to build, with the variables blanked out.
    ///
    /// Listed here on purpose, and the one list in this file that is not derived. The literal URLs
    /// below are read out of the script precisely so no second list can drift; a parameterised URL
    /// cannot be probed without knowing what to substitute, so it needs a human decision. Asserting
    /// the set is exactly this one is what makes that decision visible: adding a fetch the suite
    /// cannot probe fails the build until someone says how to probe it, rather than quietly
    /// dropping out of coverage.
    /// </summary>
    private static readonly string[] ExpectedParameterisedUrls =
    [
        "/portcalls?limit={}",
        "/portcalls/{}/laytime?{}",
        "/ports/{}",
        "/ports/{}?waitingHours={}",
        "/stops?limit={}",
    ];

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

    [Fact]
    public async Task EveryAssetThePageReferencesIsServed()
    {
        // Read out of the page rather than listed here, so a stylesheet or script added to the
        // markup and never deployed fails rather than going unnoticed.
        var page = await ReadAsset("/");

        var assets = Regex.Matches(page, @"(?:href|src)=""(?!#|https?:)([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(assets);

        using var client = _fixture.CreateClient();
        foreach (var asset in assets)
        {
            var response = await client.GetAsync(asset);
            Assert.True(
                response.IsSuccessStatusCode,
                $"the page references {asset}, which returned {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task EveryLiteralUrlTheScriptFetchesAnswers()
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
    public async Task EveryParameterisedUrlTheScriptBuildsIsOneTheSuiteProbes()
    {
        // A template literal is invisible to the literal scan above. Without this, moving a URL
        // into a template -- which the laytime and benchmark views require -- would drop it out of
        // coverage silently, which is the failure this file exists to prevent.
        var script = await ReadAsset("/app.js");

        var built = Regex.Matches(script, @"(?:json|probe)\(`([^`]+)`\)")
            .Select(m => Regex.Replace(m.Groups[1].Value, @"\$\{[^}]*\}", "{}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(ExpectedParameterisedUrls.Order(StringComparer.Ordinal), built);
    }

    [Fact]
    public async Task TheParameterisedUrlsResolveAgainstRealIdentifiers()
    {
        using var client = _fixture.CreateClient();

        var calls = await client.GetFromJsonAsync<List<PortCallRow>>("/portcalls?limit=10");
        var ports = await client.GetFromJsonAsync<List<PortRow>>("/ports");

        Assert.NotNull(calls);
        Assert.NotNull(ports);
        Assert.NotEmpty(calls);
        Assert.NotEmpty(ports);

        var wpi = ports[0].WpiNumber;

        foreach (var url in new[]
        {
            "/portcalls?limit=100",
            "/stops?limit=100",
            $"/ports/{wpi}",
            $"/ports/{wpi}?waitingHours=40",
        })
        {
            var response = await client.GetAsync(url);
            Assert.True(
                response.IsSuccessStatusCode,
                $"the page builds {url}, which returned {(int)response.StatusCode}");
        }

        // 200 or 422, and nothing else. A call the pipeline declines to price is an answer the page
        // renders as a refusal (ADR-0047), so demanding success here would fail on correct
        // behaviour -- but a 404 would mean the id was built wrong and a 500 would mean the
        // endpoint broke, and neither may pass.
        foreach (var call in calls)
        {
            var response = await client.GetAsync(
                $"/portcalls/{call.Id}/laytime?allowedHours=72&ratePerDay=28000&turnHours=6");

            Assert.True(
                response.StatusCode is HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity,
                $"laytime for call {call.Id} returned {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task ThePageDoesNotReachForPositionsOrAWriteSurface()
    {
        // ADR-0037's two scope limits, asserted rather than trusted, and re-asserted by ADR-0047
        // now that the page carries a form. Positions are the read-path problem ADR-0029 warns
        // about; a write would cross its second trigger and make authentication non-deferrable
        // (ADR-0017). The terms form is a GET that builds a URL, and these assertions are what
        // keep it one.
        var script = await ReadAsset("/app.js");
        var page = await ReadAsset("/");

        Assert.DoesNotContain("/fixes", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("position_report", script, StringComparison.OrdinalIgnoreCase);

        foreach (var verb in new[] { "POST", "PUT", "PATCH", "DELETE" })
        {
            Assert.DoesNotContain($"'{verb}'", script, StringComparison.Ordinal);
            Assert.DoesNotContain($"\"{verb}\"", script, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("FormData", script, StringComparison.Ordinal);
        Assert.DoesNotContain("sendBeacon", script, StringComparison.Ordinal);

        // A form with no method attribute submits as GET, but only if nothing overrides it. Any
        // method= on a form in this page would be a write surface arriving through the markup
        // rather than through the script.
        Assert.DoesNotContain("method=", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThePageStatesNoThresholdOfItsOwn()
    {
        // The page prints six thresholds -- why a call was excluded, why a median is absent, why a
        // p90 is, and the three charter party defaults. Each is fetched from /meta, which projects
        // the C# constants, so the only way to change one is to change the constant (ADR-0047).
        // A JavaScript copy is a caption that can go stale beside a live figure.
        var script = await ReadAsset("/app.js");

        Assert.DoesNotContain("PLAUSIBLY_AT_PORT_NM", script, StringComparison.Ordinal);
        Assert.Contains("json('/meta')", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRefusalIsRenderedFromTheServersOwnSentence()
    {
        // LaytimeAssessor writes one sentence so the CLI and the page cannot disagree about what is
        // missing. A second copy in JavaScript is the drift the shared assessor exists to prevent,
        // so the script reads `detail` off the body and does not restate it.
        var script = await ReadAsset("/app.js");

        Assert.Contains("body.detail", script, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "no berth phase, so there are no cargo operations",
            script,
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> ReadAsset(string path)
    {
        using var client = _fixture.CreateClient();
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private sealed record PortCallRow(long Id);

    private sealed record PortRow(int WpiNumber);
}
