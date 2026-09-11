using AisPipeline.Core.Domain;
using AisPipeline.Core.Laytime;

namespace AisPipeline.Tests.Laytime;

public class VoyageTimelineTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static StopEvent Stop(double startHour, double hours) => new()
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
        FirstPositionId = 1,
        LastPositionId = 2,
    };

    private static PortCall Call(params (StopPhase Phase, double Start, double Hours)[] phases) => new()
    {
        Mmsi = 219000001,
        Phases = [.. phases.Select((p, i) => new PortCallPhase(i, p.Phase, Stop(p.Start, p.Hours)))],
    };

    [Fact]
    public void AnchorageThenBerthYieldsTheTimestampsLaytimeNeeds()
    {
        var timeline = VoyageTimeline.FromPortCall(Call(
            (StopPhase.Anchorage, 0, 19.3),
            (StopPhase.Berth, 21, 31.8)))!;

        Assert.Equal(T0, timeline.ArrivedUtc);
        Assert.Equal(T0.AddHours(21), timeline.BerthedUtc);
        Assert.Equal(T0.AddHours(52.8), timeline.DepartedBerthUtc);
        Assert.Equal(21.0, timeline.HoursWaitingForBerth, 6);
    }

    [Fact]
    public void ACallWithNoBerthHasNoTimelineRatherThanAGuessedOne()
    {
        // A vessel that only ever lay at anchor has no cargo operations to measure. Inventing a
        // berth time from an anchorage would put a fabricated timestamp into a commercial claim.
        Assert.Null(VoyageTimeline.FromPortCall(Call((StopPhase.Anchorage, 0, 40))));
    }

    [Fact]
    public void BerthingSpansFromTheFirstBerthToTheLast()
    {
        // A vessel that shifts berth mid-call is still one period of cargo operations for this
        // purpose; whether the shifting time counts is a charter party question.
        var timeline = VoyageTimeline.FromPortCall(Call(
            (StopPhase.Berth, 0, 10),
            (StopPhase.Anchorage, 11, 2),
            (StopPhase.Berth, 14, 8)))!;

        Assert.Equal(T0, timeline.BerthedUtc);
        Assert.Equal(T0.AddHours(22), timeline.DepartedBerthUtc);
    }

    [Fact]
    public void AnUnknownPhaseIsNotTreatedAsABerth()
    {
        // Geometry that could not be trusted must not become cargo operations (ADR-0025).
        Assert.Null(VoyageTimeline.FromPortCall(Call((StopPhase.Unknown, 0, 40))));
    }
}
