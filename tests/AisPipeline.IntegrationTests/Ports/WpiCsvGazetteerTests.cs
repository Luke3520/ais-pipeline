using AisPipeline.Adapters.Csv;
using AisPipeline.Core.Geo;

namespace AisPipeline.IntegrationTests.Ports;

/// <summary>
/// The gazetteer reader, and the committed extract it reads.
///
/// The file is checked in, so these assert against the real thing rather than a stand-in. A
/// gazetteer that silently loses rows or misparses a coordinate would not throw -- it would just
/// name the wrong port, or none.
/// </summary>
public class WpiCsvGazetteerTests
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

    private static string CommittedPath() => Path.Combine(RepoRoot(), WpiCsvGazetteer.DefaultPath);

    [Fact]
    public void TheCommittedExtractLoadsWhereTheDefaultPathSaysItIs()
    {
        // The path the CLI defaults to. If this moves and nothing notices, `detect` quietly stops
        // naming ports and prints its warning instead.
        Assert.True(File.Exists(CommittedPath()), $"no gazetteer at {CommittedPath()}");

        var ports = WpiCsvGazetteer.Load(CommittedPath());

        Assert.Equal(145, ports.Count);
        Assert.All(ports, p =>
        {
            Assert.NotEqual(0, p.WpiNumber);
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.False(string.IsNullOrWhiteSpace(p.Country));

            // Inside the bounding box the README documents. A coordinate parsed under a Danish
            // locale -- comma as decimal separator -- would land far outside it rather than
            // throwing (docs/rules/units-and-geodesy.md).
            Assert.InRange(p.LatitudeDeg, 53.0, 59.0);
            Assert.InRange(p.LongitudeDeg, 6.0, 15.0);
        });
    }

    [Fact]
    public void TheExtractCoversTheCountriesTheFeedActuallyVisits()
    {
        // The three busiest stop clusters in the window are Goteborg, Kiel and Rostock -- none of
        // them Danish. A Denmark-only gazetteer resolves none of them (ADR-0034).
        var byCountry = WpiCsvGazetteer.Load(CommittedPath())
            .GroupBy(p => p.Country)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.True(byCountry.ContainsKey("Denmark"));
        Assert.True(byCountry.ContainsKey("Germany"));
        Assert.True(byCountry.ContainsKey("Sweden"));
    }

    [Fact]
    public void TheBusiestObservedSitesResolveToTheExpectedPorts()
    {
        var index = new NearestPortIndex(WpiCsvGazetteer.Load(CommittedPath()));

        // Centroids measured from the seven-day window, with the ports ADR-0034 records for them.
        Assert.Equal("Goteborg", index.Nearest(57.69, 11.88)?.Name);
        Assert.Equal("Frederikshavn", index.Nearest(57.43, 10.56)?.Name);
        Assert.Equal("Kiel", index.Nearest(54.37, 10.14)?.Name);
        Assert.Equal("Rostock", index.Nearest(54.16, 12.13)?.Name);
    }

    [Fact]
    public void TheTwoGridCellsThatSplitOneSiteResolveToOnePort()
    {
        // The defect a gazetteer exists to fix: 13 calls and 11 calls counted as separate places
        // because they sat either side of a 0.01 degree boundary.
        var index = new NearestPortIndex(WpiCsvGazetteer.Load(CommittedPath()));

        var first = index.Nearest(57.69, 11.88);
        var second = index.Nearest(57.69, 11.87);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.WpiNumber, second!.WpiNumber);
    }

    [Fact]
    public void AMalformedCoordinateIsRefusedRatherThanDefaulted()
    {
        // A row that will not parse must not become a port at 0,0. Null island sits 3,400 nm from
        // everything here, so it would never win a comparison and the loss would be silent.
        var path = Path.Combine(Path.GetTempPath(), $"wpi-bad-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, """
            wpi_number,port_name,alternate_name,un_locode,country,latitude_deg,longitude_deg
            25620,Arhus,,DKAAR,Denmark,56;150000,10.216667
            """);

        try
        {
            var error = Assert.Throws<InvalidDataException>(() => WpiCsvGazetteer.Load(path));
            Assert.Contains("latitude_deg", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AGazetteerWithNoRowsIsRefused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wpi-empty-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path,
            "wpi_number,port_name,alternate_name,un_locode,country,latitude_deg,longitude_deg\n");

        try
        {
            Assert.Throws<InvalidDataException>(() => WpiCsvGazetteer.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
