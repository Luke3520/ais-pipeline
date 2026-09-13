using AisPipeline.Core.Export;
using AisPipeline.Core.Quality;
using AisPipeline.Core.Query;

namespace AisPipeline.Tests.Export;

/// <summary>
/// The shape a published site depends on.
///
/// Assembled in Core and tested here so the contract is decided rather than emerging from whatever
/// the CLI happened to serialise (ADR-0039).
/// </summary>
public class ExportBuilderTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static VesselStopStatus Stops(long mmsi, long lapses, long total, double hours = 1.0) =>
        new() { Mmsi = mmsi, Lapses = lapses, TotalStops = total, HoursClaimingUnderWay = hours };

    private static VesselFixStatus Fixes(long mmsi, long lapses, long total, double fastest = 9.0) =>
        new() { Mmsi = mmsi, Lapses = lapses, TotalFixes = total, FastestKn = fastest };

    private static ExportDocument Build(
        IReadOnlyList<VesselStopStatus> stopStatus,
        IReadOnlyList<VesselFixStatus> fixStatus,
        IReadOnlyDictionary<long, StoredVessel>? vessels = null,
        IReadOnlyList<StoredRun>? runs = null) =>
        ExportBuilder.Build(
            T0,
            runs ??
            [new StoredRun { Id = 1, SourceFile = "day-1.zip", RowsRead = 100, RowsInserted = 40 },
             new StoredRun { Id = 2, SourceFile = "day-2.zip", RowsRead = 60, RowsInserted = 20 }],
            [],
            new StopStatusDisagreement { TotalStops = 10, Disagreeing = 4 },
            stopStatus,
            fixStatus,
            vessels ?? new Dictionary<long, StoredVessel>(),
            portCalls: 3,
            firstFixUtc: T0,
            lastFixUtc: T0.AddDays(7));

    [Fact]
    public void A_vessel_appearing_in_both_lists_becomes_one_record()
    {
        // The merge is what makes "forgot in both directions" answerable. Concatenating would give
        // the same vessel two rows and no way to notice it is the same ship.
        var document = Build([Stops(1, 2, 4)], [Fixes(1, 30, 900)]);

        var vessel = Assert.Single(document.Vessels);
        Assert.Equal(2, vessel.ArrivalLapses);
        Assert.Equal(30, vessel.DepartureLapses);
        Assert.True(vessel.BothDirections);
    }

    [Fact]
    public void A_vessel_in_only_one_list_is_not_claimed_to_have_done_both()
    {
        var document = Build([Stops(1, 2, 4)], [Fixes(2, 30, 900)]);

        Assert.Equal(2, document.Vessels.Count);
        Assert.All(document.Vessels, v => Assert.False(v.BothDirections));
    }

    [Fact]
    public void Vessels_that_never_lapsed_stay_out_of_the_document_but_count_in_the_summary()
    {
        // They are needed to count the always-right population and would otherwise add thousands
        // of empty records to a file a browser downloads.
        var document = Build([Stops(1, 0, 8), Stops(2, 3, 5)], []);

        Assert.Equal(2, Assert.Single(document.Vessels).Mmsi);
        Assert.Equal(1, document.Summary.VesselsAlwaysRight);
    }

    [Fact]
    public void Never_updated_needs_enough_stops_to_be_a_habit()
    {
        // One stop is 0% or 100% and neither means anything. The claim is about repetition.
        var document = Build(
            [Stops(1, 1, 1), Stops(2, ExportSummary.HabitMinimumStops, ExportSummary.HabitMinimumStops)],
            []);

        Assert.False(document.Vessels.Single(v => v.Mmsi == 1).NeverUpdated);
        Assert.True(document.Vessels.Single(v => v.Mmsi == 2).NeverUpdated);
        Assert.Equal(1, document.Summary.VesselsAlwaysWrong);
    }

    [Fact]
    public void Rates_are_derived_from_the_denominator_not_stored()
    {
        var document = Build([Stops(1, 3, 12)], [Fixes(1, 50, 1000)]);

        var vessel = Assert.Single(document.Vessels);
        Assert.Equal(25.0, vessel.ArrivalSharePercent, 3);
        Assert.Equal(5.0, vessel.DepartureSharePercent, 3);
    }

    [Fact]
    public void The_summary_counts_say_what_they_count()
    {
        // Three different populations that an earlier version collapsed into one field called
        // "Vessels", which held the offender count and read on a page as the size of the dataset.
        var document = Build([Stops(1, 0, 8), Stops(2, 3, 5), Stops(3, 1, 9)], []);

        Assert.Equal(3, document.Summary.VesselsWithStops);
        Assert.Equal(2, document.Summary.VesselsWithLapses);
        Assert.Equal(2, document.Vessels.Count);
    }

    [Fact]
    public void The_manifest_carries_its_sources_in_ingest_order()
    {
        // A figure on a website is as untraceable as a row in a table unless it says which files
        // produced it.
        var document = Build([], []);

        Assert.Equal(["day-1.zip", "day-2.zip"], document.Manifest.SourceFiles);
        Assert.Equal(160, document.Manifest.RowsRead);
        Assert.Equal(60, document.Manifest.RowsStored);
        Assert.Equal(T0, document.Manifest.GeneratedUtc);
    }

    [Fact]
    public void A_file_ingested_twice_is_listed_once_and_counted_once()
    {
        // Re-running the refresh on a file already in the store is a no-op for the data but still
        // records a run, by design. Summing runs made the manifest claim eight source files where
        // seven exist, and inflated "rows read" by a whole day -- 149.6M against an actual 127.6M.
        var document = Build([], [], runs:
        [
            new StoredRun { Id = 1, SourceFile = "day-1.zip", RowsRead = 100, RowsInserted = 40 },
            new StoredRun { Id = 2, SourceFile = "day-2.zip", RowsRead = 60, RowsInserted = 20 },
            new StoredRun { Id = 3, SourceFile = "day-2.zip", RowsRead = 60, RowsInserted = 0 },
        ]);

        Assert.Equal(["day-1.zip", "day-2.zip"], document.Manifest.SourceFiles);

        // The feed held 160 rows however many times it was offered.
        Assert.Equal(160, document.Manifest.RowsRead);

        // Stored is already immune: the re-ingest inserted nothing.
        Assert.Equal(60, document.Manifest.RowsStored);
    }

    [Fact]
    public void Files_are_listed_in_the_order_they_were_first_ingested()
    {
        var document = Build([], [], runs:
        [
            new StoredRun { Id = 1, SourceFile = "day-1.zip", RowsRead = 10, RowsInserted = 5 },
            new StoredRun { Id = 2, SourceFile = "day-2.zip", RowsRead = 10, RowsInserted = 5 },
            new StoredRun { Id = 3, SourceFile = "day-1.zip", RowsRead = 10, RowsInserted = 0 },
            new StoredRun { Id = 4, SourceFile = "day-3.zip", RowsRead = 10, RowsInserted = 5 },
        ]);

        // Not re-ordered by the re-ingest: day-1 keeps its original place.
        Assert.Equal(["day-1.zip", "day-2.zip", "day-3.zip"], document.Manifest.SourceFiles);
        Assert.Equal(30, document.Manifest.RowsRead);
    }

    [Fact]
    public void A_vessel_name_is_carried_when_known_and_null_when_not()
    {
        // The name is itself a hand-typed field, and 1,166 vessels in the feed never broadcast one.
        var document = Build(
            [Stops(1, 1, 2), Stops(2, 1, 2)],
            [],
            new Dictionary<long, StoredVessel> { [1] = new() { Mmsi = 1, Name = "TRESFJORD" } });

        Assert.Equal("TRESFJORD", document.Vessels.Single(v => v.Mmsi == 1).Name);
        Assert.Null(document.Vessels.Single(v => v.Mmsi == 2).Name);
    }

    [Fact]
    public void Vessels_are_ordered_by_total_lapses_so_the_worst_lead()
    {
        var document = Build([Stops(1, 1, 5), Stops(2, 9, 10)], [Fixes(3, 100, 500)]);

        Assert.Equal([3, 2, 1], document.Vessels.Select(v => v.Mmsi).ToList());
    }
}
