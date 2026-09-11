using AisPipeline.Core.Domain;

namespace AisPipeline.Tests.Domain;

public class VesselTests
{
    private static RawAisRecord Record(
        string? shipType = null,
        string? name = null,
        string? imo = null,
        int day = 5,
        int hour = 12) => new()
        {
            SourceFile = "f.csv",
            SourceLine = 2,
            RawText = "raw",
            TimestampUtc = new DateTime(2026, 9, day, hour, 0, 0, DateTimeKind.Utc),
            TypeOfMobile = "Class A",
            Mmsi = 219005866,
            Latitude = 56.0,
            Longitude = 10.0,
            NavigationalStatus = "Under way using engine",
            ShipType = shipType,
            Name = name,
            Imo = imo,
        };

    [Fact]
    public void APositionRowCarryingNoStaticDataDoesNotEraseWhatIsKnown()
    {
        // This is the whole reason for two-pass identity resolution (ADR-0007). Static data
        // rides only on message-type-5 rows; if a later position row overwrote it with null,
        // a tanker would stop being recognisable as one and its fixes would be filtered out.
        var known = Vessel.From(Record(shipType: "Tanker", name: "NORDIC", imo: "9123456"));

        var merged = known.MergeWith(Record(shipType: null, name: null, imo: null));

        Assert.Equal("Tanker", merged.ShipType);
        Assert.Equal("NORDIC", merged.Name);
        Assert.Equal("9123456", merged.Imo);
    }

    [Fact]
    public void LaterStaticDataWins()
    {
        var vessel = Vessel.From(Record(name: "OLD"));

        Assert.Equal("NEW", vessel.MergeWith(Record(name: "NEW")).Name);
    }

    [Fact]
    public void FirstAndLastSeenWidenRatherThanFollowRowOrder()
    {
        // Rows arrive in file order, which is globally time-sorted but not per vessel across
        // files. Taking the newest timestamp blindly would make first_seen drift forward.
        var vessel = Vessel.From(Record(day: 5, hour: 12));

        var widened = vessel
            .MergeWith(Record(day: 3, hour: 6))
            .MergeWith(Record(day: 7, hour: 23));

        Assert.Equal(new DateTime(2026, 9, 3, 6, 0, 0, DateTimeKind.Utc), widened.FirstSeenUtc);
        Assert.Equal(new DateTime(2026, 9, 7, 23, 0, 0, DateTimeKind.Utc), widened.LastSeenUtc);
    }

    [Fact]
    public void MergingASingleRowLeavesTheBoundsUnchanged()
    {
        var vessel = Vessel.From(Record(day: 5, hour: 12));
        var merged = vessel.MergeWith(Record(day: 5, hour: 12));

        Assert.Equal(vessel.FirstSeenUtc, merged.FirstSeenUtc);
        Assert.Equal(vessel.LastSeenUtc, merged.LastSeenUtc);
    }
}
