using AisPipeline.Core.Domain;

namespace AisPipeline.Tests.Detection;

/// <summary>
/// What a reported draught change is allowed to claim.
///
/// The claim is deliberately narrow: a crew said its draught changed. Draught arrives in the same
/// hand-typed voyage message as the destination and the ETA, so this is evidence about cargo
/// exactly as far as the crew is reliable and no further (ADR-0048). Nothing here folds it into
/// waiting or working hours, which come from measured position alone.
/// </summary>
public class ReportedCargoMovementTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static StopEvent Stop(double startHour, double hours, double? first, double? last) => new()
    {
        Mmsi = 219000001,
        StartedUtc = T0.AddHours(startHour),
        EndedUtc = T0.AddHours(startHour + hours),
        CentroidLatitude = 56.0,
        CentroidLongitude = 10.0,
        MaxDriftNm = 0.002,
        FixCount = 100,
        ReliableFixCount = 100,
        ReportedStatus = "Moored",
        StatusAgrees = true,
        IsComplete = true,
        DraughtFirstM = first,
        DraughtLastM = last,
        FirstPositionId = 1,
        LastPositionId = 2,
    };

    private static PortCall Call(params StopEvent[] stops) => new()
    {
        Mmsi = 219000001,
        Phases = [.. stops.Select((s, i) => new PortCallPhase(i, StopPhase.Berth, s))],
    };

    [Fact]
    public void A_draught_that_fell_is_reported_as_discharged()
    {
        var call = Call(Stop(0, 20, first: 12.4, last: 7.1));

        Assert.Equal(CargoMovement.Discharged, call.ReportedCargoMovement);
        Assert.Equal(12.4, call.DraughtOnArrivalM);
        Assert.Equal(7.1, call.DraughtOnDepartureM);
    }

    [Fact]
    public void A_draught_that_rose_is_reported_as_loaded() =>
        Assert.Equal(CargoMovement.Loaded, Call(Stop(0, 20, 7.1, 12.4)).ReportedCargoMovement);

    [Theory]
    [InlineData(12.0, 12.0)]
    [InlineData(12.0, 12.5)]
    [InlineData(12.5, 12.0)]
    public void A_change_within_the_threshold_claims_nothing(double first, double last)
    {
        // Crews round: 12.0, 12.5 and 13.0 are far commoner than 12.3. A threshold under half a
        // metre would turn that rounding into a cargo operation.
        Assert.Equal(CargoMovement.Unchanged, Call(Stop(0, 20, first, last)).ReportedCargoMovement);
    }

    [Fact]
    public void No_draught_reported_at_either_end_is_unknown_and_not_unchanged()
    {
        // Different claims. "The crew said it did not change" and "the crew never said" are not
        // the same statement, and a call that never reported must not read as a quiet vessel.
        var call = Call(Stop(0, 20, null, null));

        Assert.Equal(CargoMovement.Unknown, call.ReportedCargoMovement);
        Assert.Null(call.DraughtOnArrivalM);
    }

    [Fact]
    public void Draught_is_taken_from_the_first_and_last_stop_that_reported_one()
    {
        // A call chains several stops. The arrival figure is the earliest anyone reported and the
        // departure figure the latest, so a silent berth in the middle does not erase the change.
        var call = Call(
            Stop(0, 5, first: 12.4, last: 12.4),
            Stop(6, 10, first: null, last: null),
            Stop(20, 5, first: 7.0, last: 7.0));

        Assert.Equal(12.4, call.DraughtOnArrivalM);
        Assert.Equal(7.0, call.DraughtOnDepartureM);
        Assert.Equal(CargoMovement.Discharged, call.ReportedCargoMovement);
    }

    [Fact]
    public void Reporting_only_on_arrival_claims_nothing()
    {
        // Half a measurement is not a measurement. Treating the missing end as unchanged would
        // manufacture a claim from a silence.
        Assert.Equal(
            CargoMovement.Unknown,
            Call(Stop(0, 20, first: 12.4, last: null)).ReportedCargoMovement);
    }

    [Fact]
    public void The_threshold_is_named_and_not_an_inline_literal() =>
        Assert.Equal(0.5, CargoThresholds.ReportedChangeM);
}
