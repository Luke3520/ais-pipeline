using AisPipeline.Core.Domain;
using AisPipeline.Core.Laytime;
using AisPipeline.Core.Reconciliation;
using AisPipeline.Core.Sof;

namespace AisPipeline.Tests.Reconciliation;

/// <summary>
/// Two accounts of the same port call, compared. Neither is declared correct -- the same position
/// rule R10 takes when a vessel's status contradicts its own speed, with higher stakes.
/// </summary>
public class TimelineReconcilerTests
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

    /// <summary>Anchorage 0–20h, berth 22–70h — the shape AIS produces.</summary>
    private static PortCall AisCall() => new()
    {
        Mmsi = 219000001,
        Phases =
        [
            new PortCallPhase(0, StopPhase.Anchorage, Stop(0, 20)),
            new PortCallPhase(1, StopPhase.Berth, Stop(22, 48)),
        ],
    };

    private static SofEvent Event(SofEventKind kind, string label, double hour, string? remark = null) =>
        new(T0.AddHours(hour), kind, label, remark);

    /// <summary>
    /// A document agreeing with AIS: anchored and aweigh at the observed times, and "all fast"
    /// 45 minutes after AIS saw the vessel stop -- the lag two real documents exhibit.
    /// </summary>
    private static StatementOfFacts AgreeingSof(params SofEvent[] extra) => new()
    {
        Mmsi = 219000001,
        VesselName = "TEST",
        Port = "Fredericia",
        Events =
        [
            Event(SofEventKind.Anchored, "Dropped anchor", 0),
            Event(SofEventKind.AnchorAweigh, "Anchor aweigh", 20),
            Event(SofEventKind.AllFast, "All fast", 22.75),
            Event(SofEventKind.CargoCommenced, "Commenced discharging", 24),
            Event(SofEventKind.CargoCompleted, "Completed discharging", 68),
            Event(SofEventKind.LeftBerth, "Left berth (last line)", 70),
            .. extra,
        ],
    };

    private static CharterPartyTerms Terms(double allowed = 72.0) => new()
    {
        LaytimeAllowedHours = allowed,
        DemurrageRatePerDay = Money.FromMajor(28_000m, "USD"),
        NoticeOfReadinessUtc = T0,
        TurnTimeHours = 6.0,
    };

    private static readonly TimelineReconciler Reconciler = new();

    [Fact]
    public void AnHonestDocumentAgreesOnEveryObservableEvent()
    {
        var result = Reconciler.Reconcile(AgreeingSof(), AisCall(), Terms());

        Assert.Empty(result.Disagreements);
        Assert.All(
            result.Comparisons.Where(c => c.Kind is SofEventKind.Anchored or SofEventKind.AnchorAweigh
                or SofEventKind.AllFast or SofEventKind.LeftBerth),
            c => Assert.Equal(ComparisonVerdict.Agrees, c.Verdict));
    }

    [Fact]
    public void AllFastIsNotExpectedToMatchTheMomentAisSeesTheVesselStop()
    {
        // The heart of it. A vessel stops moving well before it is made fast, so comparing the two
        // and expecting zero would flag every honest document ever written.
        var result = Reconciler.Reconcile(AgreeingSof(), AisCall(), Terms());
        var allFast = result.Comparisons.Single(c => c.Kind == SofEventKind.AllFast);

        Assert.Equal(45, allFast.RawDelta!.Value.TotalMinutes, 1);
        Assert.Equal(0, allFast.UnexplainedDelta!.Value.TotalMinutes, 1);
        Assert.Equal(ComparisonVerdict.Agrees, allFast.Verdict);
    }

    [Theory]
    [InlineData(22.5, ComparisonVerdict.Agrees)]    // 30 min lag -- the Immingham figure
    [InlineData(23.017, ComparisonVerdict.Agrees)]  // 61 min lag -- the Chimbote figure
    [InlineData(24.5, ComparisonVerdict.Disagrees)] // 2.5h after the stop: outside anything observed
    [InlineData(22.0, ComparisonVerdict.Disagrees)] // all fast BEFORE the vessel stopped moving
    public void TheAllFastBandCoversWhatRealDocumentsShowAndNotMore(
        double allFastHour, ComparisonVerdict expected)
    {
        var sof = new StatementOfFacts
        {
            Mmsi = 219000001,
            VesselName = "TEST",
            Port = "Fredericia",
            Events =
            [
                Event(SofEventKind.AllFast, "All fast", allFastHour),
                Event(SofEventKind.LeftBerth, "Left berth (last line)", 70),
            ],
        };

        var result = Reconciler.Reconcile(sof, AisCall(), Terms());

        Assert.Equal(expected, result.Comparisons.Single(c => c.Kind == SofEventKind.AllFast).Verdict);
    }

    [Fact]
    public void EventsAisCannotSeeAreReportedRatherThanOmitted()
    {
        // Silence must not read as agreement. A notice is an email; hoses are not visible.
        var result = Reconciler.Reconcile(AgreeingSof(
            Event(SofEventKind.NoticeOfReadinessTendered, "NOR tendered", 0.5)), AisCall(), Terms());

        var uncorroborated = result.Uncorroborated.Select(c => c.Kind).ToList();

        Assert.Contains(SofEventKind.NoticeOfReadinessTendered, uncorroborated);
        Assert.Contains(SofEventKind.CargoCommenced, uncorroborated);
        Assert.Contains(SofEventKind.CargoCompleted, uncorroborated);
    }

    private static StatementOfFacts ClaimingEarlyBerthing() => new()
    {
        Mmsi = 219000001,
        VesselName = "TEST",
        Port = "Fredericia",
        Events =
        [
            Event(SofEventKind.AllFast, "All fast", 18),
            Event(SofEventKind.LeftBerth, "Left berth (last line)", 70),
        ],
    };

    [Fact]
    public void TheSameDiscrepancyIsWorthMoneyOrNothingDependingOnTheTerms()
    {
        // The pair that makes per-delta pricing indefensible. One document, one AIS timeline, one
        // four-hour disagreement about berthing -- and two answers, because what a discrepancy
        // COSTS is a property of the charter party, not of the discrepancy.
        var sof = ClaimingEarlyBerthing();

        // Laytime starts on berthing: the disagreement moves the clock, so it is worth money.
        var onBerthing = Reconciler.Reconcile(
            sof, AisCall(),
            Terms(allowed: 20) with { Commencement = LaytimeCommencement.OnBerthing });

        Assert.True(onBerthing.DemurrageDifference.MinorUnits > 0,
            "an earlier claimed berthing should price higher when laytime starts on berthing");

        // Laytime starts after turn time, which expires long before either berthing time. Both
        // timelines commence at the same moment, so the same four hours are worth exactly nothing.
        var turnTime = Reconciler.Reconcile(sof, AisCall(), Terms(allowed: 20));

        Assert.Equal(0, turnTime.DemurrageDifference.MinorUnits);
        Assert.Single(turnTime.Disagreements);
    }

    [Fact]
    public void ADiscrepancyIsAlsoWorthNothingWhenNeitherTimelineReachesDemurrage()
    {
        var result = Reconciler.Reconcile(
            ClaimingEarlyBerthing(), AisCall(),
            Terms(allowed: 500) with { Commencement = LaytimeCommencement.OnBerthing });

        Assert.Single(result.Disagreements);
        Assert.Equal(0, result.DemurrageDifference.MinorUnits);
    }

    [Fact]
    public void SuspensionsInTheDocumentBecomeExceptedPeriodsInItsOwnStatement()
    {
        var result = Reconciler.Reconcile(
            AgreeingSof(
                Event(SofEventKind.Suspended, "Suspended discharging", 30, "shore's stop"),
                Event(SofEventKind.Resumed, "Resumed discharging", 36)),
            AisCall(),
            // Generous enough that the vessel is still within laytime when the suspension starts.
            // Under a tight allowance it would already be on demurrage by then, and the exception
            // would not deduct at all -- "once on demurrage, always on demurrage".
            Terms(allowed: 72));

        Assert.Equal(6.0, result.FromStatementOfFacts.ExceptedHours, 1);
        Assert.Equal(0.0, result.FromAis.ExceptedHours, 1);

        // AIS knows nothing of shore stops, so its timeline counts those six hours where the
        // document excepts them. That gap IS the finding.
        Assert.True(result.FromAis.UsedHours > result.FromStatementOfFacts.UsedHours);
    }

    [Fact]
    public void AnUntrustworthyBerthSpanIsRefusedRatherThanCompared()
    {
        var call = new PortCall
        {
            Mmsi = 219000001,
            Phases =
            [
                new PortCallPhase(0, StopPhase.Berth, Stop(0, 10)),
                new PortCallPhase(1, StopPhase.Unknown,
                    Stop(11, 4) with { FixCount = 200, ReliableFixCount = 1 }),
                new PortCallPhase(2, StopPhase.Berth, Stop(16, 8)),
            ],
        };

        Assert.Throws<ArgumentException>(() => Reconciler.Reconcile(AgreeingSof(), call, Terms()));
    }

    [Fact]
    public void ADocumentWithNoEndToLaytimeIsRefusedRatherThanAssumed()
    {
        var sof = new StatementOfFacts
        {
            Mmsi = 219000001,
            VesselName = "TEST",
            Port = "Fredericia",
            Events = [Event(SofEventKind.AllFast, "All fast", 22.75)],
        };

        Assert.Throws<ArgumentException>(() => Reconciler.Reconcile(sof, AisCall(), Terms()));
    }
}
