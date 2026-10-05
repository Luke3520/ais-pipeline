using AisPipeline.Adapters.Csv;
using AisPipeline.Core.Radar;

namespace AisPipeline.IntegrationTests.Ports;

/// <summary>
/// The coastline reader, against the committed extract. Scenery, but scenery that silently lost a
/// line or misparsed a vertex would draw a coast with a hole in it and nobody would know why.
/// </summary>
public class CoastlineCsvTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AisPipeline.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void TheCommittedCoastlineLoadsWithTheCountsItsReadmeStates()
    {
        var lines = CoastlineCsv.Load(Path.Combine(RepoRoot(), CoastlineCsv.DefaultPath));

        Assert.Equal(57, lines.Count);
        Assert.Equal(3_186, lines.Sum(l => l.Count));
        Assert.All(lines, l => Assert.True(l.Count >= 2, "a line needs two vertices"));

        // Inside the clip box the README documents. A vertex read under a Danish locale would land
        // far outside it rather than throw.
        Assert.All(lines.SelectMany(l => l), v =>
        {
            Assert.InRange(v.LatDeg, 53.0, 60.0);
            Assert.InRange(v.LonDeg, 3.0, 16.0);
        });
    }

    [Fact]
    public void EveryNamedRegionHasCoastInsideIt()
    {
        // A region box that missed the coastline entirely would draw open sea where there is land.
        var vertices = CoastlineCsv.Load(Path.Combine(RepoRoot(), CoastlineCsv.DefaultPath))
            .SelectMany(l => l).ToList();

        Assert.All(RadarRegions.ByName, region =>
            Assert.Contains(vertices, v => region.Value.Contains(v.LatDeg, v.LonDeg)));
    }

    [Fact]
    public void AVertexThatWillNotParseIsRefusedNotSkipped()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "line,lon,lat\n0,10.5,56.1\n0,10.6,fifty-six\n");

            var thrown = Assert.Throws<InvalidDataException>(() => CoastlineCsv.Load(path));
            Assert.Contains("line 3", thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
