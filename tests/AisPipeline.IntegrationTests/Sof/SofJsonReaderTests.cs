using AisPipeline.Adapters.Sof;
using AisPipeline.Core.Sof;

namespace AisPipeline.IntegrationTests.Sof;

/// <summary>
/// The reader is the boundary between a document someone typed and a figure someone invoices. It
/// refuses rather than defaults, because every default here is a guess about a commercial fact.
/// </summary>
public class SofJsonReaderTests
{
    private const string Minimal = """
        {
          "mmsi": 219000001,
          "vesselName": "TEST",
          "port": "Fredericia",
          "events": [
            { "timestampUtc": "2026-09-01T08:00:00Z", "label": "Dropped anchor" },
            { "timestampUtc": "2026-09-01T20:00:00Z", "label": "All fast" }
          ]
        }
        """;

    [Fact]
    public void ReadsAStatementAndClassifiesItsLabels()
    {
        var sof = SofJsonReader.Read(Minimal);

        Assert.Equal(219000001, sof.Mmsi);
        Assert.Equal(SofEventKind.Anchored, sof.Events[0].Kind);
        Assert.Equal(SofEventKind.AllFast, sof.Events[1].Kind);
    }

    [Fact]
    public void TheAgentsOwnWordsAreKeptAlongsideTheClassification()
    {
        // The wording is what a dispute turns on. Replacing it with an enum would throw away the
        // evidence and keep only our reading of it.
        var sof = SofJsonReader.Read(Minimal);

        Assert.Equal("Dropped anchor", sof.Events[0].Label);
    }

    [Fact]
    public void EventsAreOrderedByTimeRegardlessOfDocumentOrder()
    {
        var sof = SofJsonReader.Read("""
            {
              "mmsi": 1, "events": [
                { "timestampUtc": "2026-09-02T10:00:00Z", "label": "All fast" },
                { "timestampUtc": "2026-09-01T10:00:00Z", "label": "Dropped anchor" }
              ]
            }
            """);

        Assert.True(sof.Events[0].TimestampUtc < sof.Events[1].TimestampUtc);
    }

    [Fact]
    public void TimesAreReadAsUtcRegardlessOfHowTheyWereWritten()
    {
        // A time read an hour out shifts a laytime boundary and the money with it.
        var sof = SofJsonReader.Read("""
            {"mmsi": 1, "events": [{"timestampUtc": "2026-09-01T08:00:00+02:00", "label": "All fast"}]}
            """);

        Assert.Equal(DateTimeKind.Utc, sof.Events[0].TimestampUtc.Kind);
        Assert.Equal(new DateTime(2026, 9, 1, 6, 0, 0, DateTimeKind.Utc), sof.Events[0].TimestampUtc);
    }

    [Fact]
    public void AnExplicitKindOverridesTheLabelMatcher()
    {
        // Agents write things no pattern anticipates. A human who knows what a line means must be
        // able to say so rather than argue with the matcher.
        var sof = SofJsonReader.Read("""
            {"mmsi": 1, "events": [
              {"timestampUtc": "2026-09-01T08:00:00Z", "label": "Vessel secured at SPM", "kind": "AllFast"}]}
            """);

        Assert.Equal(SofEventKind.AllFast, sof.Events[0].Kind);
        Assert.Equal("Vessel secured at SPM", sof.Events[0].Label);
    }

    [Fact]
    public void AMissingMmsiIsRefusedRatherThanDefaulted()
    {
        // The one field paper does not carry, and the only thing matching the document to AIS. A
        // statement attached to the wrong vessel would produce a confident comparison against
        // someone else's voyage.
        var e = Assert.Throws<InvalidDataException>(() => SofJsonReader.Read("""
            {"vesselName": "TEST", "events": [{"timestampUtc": "2026-09-01T08:00:00Z", "label": "All fast"}]}
            """));

        Assert.Contains("mmsi", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AStatementWithNoEventsIsRefused() =>
        Assert.Throws<InvalidDataException>(() => SofJsonReader.Read("""{"mmsi": 1, "events": []}"""));

    [Fact]
    public void AnUnreadableTimeNamesTheEventItCameFrom()
    {
        var e = Assert.Throws<InvalidDataException>(() => SofJsonReader.Read("""
            {"mmsi": 1, "events": [{"timestampUtc": "yesterday afternoon", "label": "All fast"}]}
            """));

        Assert.Contains("All fast", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventWithNoLabelIsRefused() =>
        Assert.Throws<InvalidDataException>(() => SofJsonReader.Read("""
            {"mmsi": 1, "events": [{"timestampUtc": "2026-09-01T08:00:00Z"}]}
            """));

    [Fact]
    public void TheCommittedFixtureParsesAndClassifiesItsObservableEvents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AisPipeline.slnx")))
        {
            dir = dir.Parent;
        }

        var sof = SofJsonReader.ReadFile(
            Path.Combine(dir!.FullName, "fixtures", "statement-of-facts.json"));

        // The four events AIS can corroborate must all be present and classified, or the fixture
        // stops exercising the comparison it exists for.
        foreach (var kind in new[]
        {
            SofEventKind.Anchored, SofEventKind.AnchorAweigh,
            SofEventKind.AllFast, SofEventKind.LeftBerth,
        })
        {
            Assert.NotNull(sof.First(kind));
        }

        Assert.Single(sof.SuspensionPeriods());
    }
}
