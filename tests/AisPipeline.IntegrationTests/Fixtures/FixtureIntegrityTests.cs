using Sylvan.Data.Csv;

namespace AisPipeline.IntegrationTests.Fixtures;

/// <summary>
/// Pins fixtures/sample.csv. Every other test in this suite is written against it, so silent
/// drift in the fixture would move the ground truth underneath them. These assertions describe
/// the properties the fixture was selected for -- see fixtures/MANIFEST.md.
/// </summary>
public class FixtureIntegrityTests
{
    private const int ExpectedFieldCount = 26;

    // The DMA header line begins "# Timestamp", and Sylvan's default comment character is '#'.
    // Left at the default the reader discards the real header, promotes the first DATA row to be
    // the header, and silently loses one row from every file. Disabling comments is mandatory --
    // see docs/rules/units-and-geodesy.md.
    private static CsvDataReaderOptions Options() => new() { Comment = '\0' };

    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AisPipeline.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "fixtures", "sample.csv");
    }

    [Fact]
    public void HeaderIsTheRealHeaderAndNotTheFirstDataRow()
    {
        // Regression guard for the comment-character trap described on Options().
        using var csv = CsvDataReader.Create(FixturePath(), Options());

        Assert.Equal("# Timestamp", csv.GetName(0));
        Assert.Equal("MMSI", csv.GetName(2));
        Assert.Equal(ExpectedFieldCount, csv.FieldCount);
    }

    [Fact]
    public void EveryRowHasTwentySixFieldsWhenParsedProperly()
    {
        // A naive comma split yields 27-28 fields on quoted vessel names; a conformant reader
        // yields 26 for all of them (ADR-0008). This is the assertion that pins that difference.
        using var csv = CsvDataReader.Create(FixturePath(), Options());

        var rows = 0;
        while (csv.Read())
        {
            rows++;
            Assert.Equal(ExpectedFieldCount, csv.FieldCount);
        }

        Assert.Equal(744, rows);
    }

    [Fact]
    public void ContainsARowWhoseNameFieldHoldsAComma()
    {
        // Guards the case that breaks string.Split(','). If fixture selection ever loses these
        // rows, the parser's hardest case stops being covered and nothing else would notice.
        using var csv = CsvDataReader.Create(FixturePath(), Options());

        var withComma = 0;
        while (csv.Read())
        {
            for (var i = 0; i < csv.FieldCount; i++)
            {
                if (csv.GetString(i).Contains(',', StringComparison.Ordinal))
                {
                    withComma++;
                    break;
                }
            }
        }

        Assert.True(withComma > 0, "fixture no longer contains a quoted field holding a comma");
    }

    [Fact]
    public void ContainsAVesselThatReportsUnderWayWhileSittingStill()
    {
        // Rule R10 -- the disagreement between a vessel's own status and its own speed -- is the
        // project's central finding, at 63.8% of stationary tanker fixes. The fixture did not
        // contain a single instance of it until M4, so nothing exercised the case end to end.
        using var csv = CsvDataReader.Create(FixturePath(), Options());

        var stationaryButClaimingUnderWay = 0;
        while (csv.Read())
        {
            var sog = csv.GetString(7);
            var status = csv.GetString(5);
            if (sog.Length > 0 && double.Parse(sog, System.Globalization.CultureInfo.InvariantCulture) < 0.5
                && status.StartsWith("Under way", StringComparison.Ordinal))
            {
                stationaryButClaimingUnderWay++;
            }
        }

        Assert.True(stationaryButClaimingUnderWay > 0,
            "fixture no longer contains a stationary vessel reporting 'Under way'; rule R10 has no coverage");
    }

    [Fact]
    public void ContainsTheSentinelAndUnavailableValuesTheRulesAreWrittenAgainst()
    {
        // R4 rejects latitude 91; R5 flags blank SOG. Both are measured properties of the real
        // feed, and both must stay present or those rules lose their only fixture coverage.
        using var csv = CsvDataReader.Create(FixturePath(), Options());

        var latitudeSentinel = 0;
        var blankSog = 0;
        while (csv.Read())
        {
            if (csv.GetString(3) == "91.000000")
            {
                latitudeSentinel++;
            }

            if (csv.GetString(7).Length == 0)
            {
                blankSog++;
            }
        }

        Assert.True(latitudeSentinel > 0, "fixture no longer contains the latitude-91 sentinel");
        Assert.True(blankSog > 0, "fixture no longer contains a row with unavailable SOG");
    }
}
