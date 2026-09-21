using AisPipeline.Core.Domain;
using AisPipeline.Core.Export;
using AisPipeline.Core.Voyage;
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
        IReadOnlyList<StoredRun>? runs = null,
        IReadOnlyList<PortCallHours>? portCallHours = null,
        IReadOnlyList<PortCall>? callsForPricing = null,
        IReadOnlyList<EtaHorizonBin>? etaBins = null) =>
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
            lastFixUtc: T0.AddDays(7),
            portCallHours ?? [],
            callsForPricing ?? [],
            etaBins ?? []);

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

    private static PortCallHours Call(
        double waitingHours, double distanceNm = 1.0, bool isComplete = true, int wpi = 1) =>
        new()
        {
            WpiNumber = wpi,
            PortName = wpi == 1 ? "Skagen Havn" : "Fredericia",
            Country = "DK",
            WaitingHours = waitingHours,
            WorkingHours = 2.0,
            DistanceNm = distanceNm,
            IsComplete = isComplete,
        };

    [Fact]
    public void Port_benchmarks_reach_the_document_with_the_sample_behind_them()
    {
        // Five usable calls is the minimum for a median (ADR-0043), so this is the smallest
        // document that carries one at all.
        var document = Build([], [], portCallHours:
            [Call(1.0), Call(2.0), Call(3.0), Call(4.0), Call(5.0)]);

        var port = Assert.Single(document.Ports);
        Assert.Equal("Skagen Havn", port.PortName);
        Assert.Equal(5, port.UsableCalls);
        Assert.Equal(3.0, port.MedianWaitingHours);
    }

    [Fact]
    public void A_port_with_too_few_usable_calls_publishes_no_median()
    {
        // The excluded calls still reach the document. A reader who is shown "no median" and not
        // told that six calls were thrown away to get there cannot weigh the refusal.
        var document = Build([], [], portCallHours:
            [Call(1.0), Call(2.0),
             Call(3.0, distanceNm: 40.0), Call(4.0, distanceNm: 40.0),
             Call(5.0, isComplete: false)]);

        var port = Assert.Single(document.Ports);
        Assert.Equal(2, port.UsableCalls);
        Assert.Null(port.MedianWaitingHours);
        Assert.Equal(5, port.AttributedCalls);
        Assert.Equal(1, port.ExcludedIncomplete);
        Assert.Equal(2, port.ExcludedTooFar);
    }

    [Fact]
    public void An_export_with_no_attributed_calls_carries_an_empty_port_list()
    {
        // Empty, never absent. A missing key and a port list with nothing in it are different
        // claims, and the site branches on one of them.
        Assert.Empty(Build([], []).Ports);
    }

    private static StopEvent Stop(double startHour, double hours, bool trustworthy = true) => new()
    {
        Mmsi = 219000001,
        StartedUtc = T0.AddHours(startHour),
        EndedUtc = T0.AddHours(startHour + hours),
        CentroidLatitude = 56.0,
        CentroidLongitude = 10.0,
        MaxDriftNm = 0.002,
        FixCount = 100,
        ReliableFixCount = trustworthy ? 100 : 0,
        ReportedStatus = "Moored",
        StatusAgrees = true,
        IsComplete = true,
        FirstPositionId = 1,
        LastPositionId = 2,
    };

    private static PortCall Call(params (StopPhase Phase, double Start, double Hours)[] phases) =>
        new()
        {
            Mmsi = 219000001,
            Phases = [.. phases.Select((p, i) =>
                new PortCallPhase(i, p.Phase, Stop(p.Start, p.Hours, p.Phase != StopPhase.Unknown)))],
        };

    [Fact]
    public void Every_assessed_call_is_accounted_for_by_exactly_one_outcome()
    {
        // The same identity IngestCounters applies to rows. A tally that does not add up is
        // hiding a fourth outcome nobody named.
        var document = Build([], [], callsForPricing:
            [Call((StopPhase.Anchorage, 0, 10), (StopPhase.Berth, 12, 30)),
             Call((StopPhase.Anchorage, 0, 10)),
             Call((StopPhase.Berth, 0, 5), (StopPhase.Unknown, 6, 4), (StopPhase.Berth, 11, 20))]);

        var priceability = document.Priceability;

        Assert.Equal(3, priceability.CallsAssessed);
        Assert.Equal(1, priceability.Priceable);
        Assert.Equal(1, priceability.NoBerthPhase);
        Assert.Equal(1, priceability.BerthGeometryUntrustworthy);
        Assert.True(priceability.IsBalanced);
    }

    [Fact]
    public void An_export_with_no_port_calls_still_balances()
    {
        // Zero of zero. The site branches on this figure, and a document that omitted the tally
        // when there was nothing to tally would make "none priceable" indistinguishable from
        // "never asked".
        var priceability = Build([], []).Priceability;

        Assert.Equal(0, priceability.CallsAssessed);
        Assert.True(priceability.IsBalanced);
    }
}
