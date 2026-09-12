using AisPipeline.Core.Domain;
using AisPipeline.Core.Geo;
using AisPipeline.Core.Laytime;
using AisPipeline.Core.Reconciliation;
using AisPipeline.Core.Sof;

namespace AisPipeline.Tests.Reconciliation;

/// <summary>
/// Whether the document and the port call are even about the same visit.
///
/// Before this existed, reconcile matched on mmsi and "most recent complete call" alone, so a
/// statement for one visit would be priced against an unrelated one and the difference reported as
/// a finding.
/// </summary>
public class CallMatchTests
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

    /// <summary>A call running 0h to 70h from T0.</summary>
    private static PortCall Call() => new()
    {
        Mmsi = 219000001,
        Phases =
        [
            new PortCallPhase(0, StopPhase.Anchorage, Stop(0, 20)),
            new PortCallPhase(1, StopPhase.Berth, Stop(22, 48)),
        ],
    };

    private static PortCall CallAt(string portName, double distanceNm)
    {
        var call = Call();
        return call with
        {
            Attribution = new PortAttribution
            {
                WpiNumber = 25620,
                Name = portName,
                Country = "Denmark",
                DistanceNm = distanceNm,
            },
        };
    }

    private static StatementOfFacts Sof(double firstHour, double lastHour, string port = "Fredericia") => new()
    {
        Mmsi = 219000001,
        VesselName = "TEST",
        Port = port,
        Events =
        [
            new SofEvent(T0.AddHours(firstHour), SofEventKind.Anchored, "Dropped anchor", null),
            new SofEvent(T0.AddHours(lastHour), SofEventKind.LeftBerth, "Last line off", null),
        ],
    };

    [Fact]
    public void A_document_covering_the_same_window_matches()
    {
        var match = CallMatch.Evaluate(Sof(0, 70), Call());

        Assert.True(match.TimesAreConsistent);
        Assert.Equal(TimeSpan.Zero, match.Gap);
        Assert.Equal(70, match.Overlap.TotalHours, precision: 3);
    }

    [Fact]
    public void A_document_for_a_different_call_entirely_does_not_match()
    {
        // The case that was silently priced: a statement six months older than the call the
        // "most recent complete" query returns.
        var match = CallMatch.Evaluate(Sof(-4400, -4330), Call());

        Assert.False(match.TimesAreConsistent);
        Assert.True(match.Gap > TimeSpan.FromDays(170));
    }

    [Fact]
    public void Touching_windows_do_not_count_as_overlapping()
    {
        // The document ends exactly when the call begins. Zero shared time is not evidence that
        // these are the same visit, and the boundary has to fall on the refusing side or a
        // document for the immediately preceding call would pass.
        var match = CallMatch.Evaluate(Sof(-50, 0), Call());

        Assert.False(match.TimesAreConsistent);
        Assert.Equal(TimeSpan.Zero, match.Overlap);
    }

    [Fact]
    public void A_document_extending_past_the_call_still_matches_on_the_shared_part()
    {
        // Real documents bracket the call: a notice tendered before AIS sees the vessel stop, a
        // sailing line after it moves off. Partial overlap is the normal case, not an anomaly.
        var match = CallMatch.Evaluate(Sof(-6, 76), Call());

        Assert.True(match.TimesAreConsistent);
        Assert.Equal(70, match.Overlap.TotalHours, precision: 3);
    }

    [Fact]
    public void With_no_attributed_port_the_check_is_reported_as_not_done()
    {
        // Either the stop was too far from anywhere to name, or the store predates the gazetteer.
        // Both mean the same to a reader, and neither may read as agreement.
        var match = CallMatch.Evaluate(Sof(0, 70, port: "Immingham"), Call());

        Assert.Equal("Immingham", match.DocumentPort);
        Assert.False(match.PortWasChecked);
        Assert.Null(match.PortNamesAgree);
    }

    [Fact]
    public void The_document_port_and_the_attributed_port_are_compared_when_both_exist()
    {
        var match = CallMatch.Evaluate(Sof(0, 70, port: "Fredericia"), CallAt("Fredericia", 1.8));

        Assert.True(match.PortWasChecked);
        Assert.True(match.PortNamesAgree);
        Assert.Equal(1.8, match.AisPortDistanceNm);
    }

    [Fact]
    public void Accents_and_case_do_not_make_two_spellings_of_one_port_disagree()
    {
        // The World Port Index transliterates and documents do not: it writes "Arhus" where a
        // Danish document writes "Århus". A false here would be a fabricated disagreement.
        Assert.True(CallMatch.Evaluate(Sof(0, 70, port: "\u00C5rhus"), CallAt("Arhus", 0.1)).PortNamesAgree);
        Assert.True(CallMatch.Evaluate(Sof(0, 70, port: "Aarhus"), CallAt("Arhus", 0.1)).PortNamesAgree);
        Assert.True(CallMatch.Evaluate(Sof(0, 70, port: "\u00C5RHUS"), CallAt("Arhus", 0.1)).PortNamesAgree);
    }

    [Fact]
    public void A_qualified_document_name_still_matches_the_port_inside_it()
    {
        // Documents qualify: "Port of Fredericia", "Fredericia Oil Terminal".
        Assert.True(CallMatch.Evaluate(
            Sof(0, 70, port: "Port of Fredericia"), CallAt("Fredericia", 1.8)).PortNamesAgree);
    }

    [Fact]
    public void Two_genuinely_different_ports_disagree_but_nothing_refuses()
    {
        // Reported, not resolved -- and it cannot refuse, because an exonym looks exactly like
        // this: "Gothenburg" and "Goteborg" are one port and compare as different.
        var match = CallMatch.Evaluate(Sof(0, 70, port: "Immingham"), CallAt("Fredericia", 1.8));

        Assert.False(match.PortNamesAgree);
        Assert.True(match.TimesAreConsistent, "a port-name disagreement must not fail the time test");
    }

    [Fact]
    public void A_statement_with_no_events_is_refused_rather_than_matched_vacuously()
    {
        var empty = new StatementOfFacts
        {
            Mmsi = 219000001,
            VesselName = "TEST",
            Port = "Fredericia",
            Events = [],
        };

        Assert.Throws<ArgumentException>(() => CallMatch.Evaluate(empty, Call()));
    }

    [Fact]
    public void The_reconciler_refuses_a_mismatched_pair_instead_of_pricing_it()
    {
        var reconciler = new TimelineReconciler();
        var terms = new CharterPartyTerms
        {
            LaytimeAllowedHours = 72.0,
            DemurrageRatePerDay = Money.FromMajor(28_000m, "USD"),
            NoticeOfReadinessUtc = T0,
            TurnTimeHours = 6.0,
        };

        var e = Assert.Throws<ArgumentException>(
            () => reconciler.Reconcile(Sof(-4400, -4330), Call(), terms));

        Assert.Contains("do not describe the same visit", e.Message);
    }
}
