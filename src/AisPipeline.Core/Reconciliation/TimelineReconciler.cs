using AisPipeline.Core.Domain;
using AisPipeline.Core.Laytime;
using AisPipeline.Core.Sof;

namespace AisPipeline.Core.Reconciliation;

/// <summary>What the two accounts disagree about, and what the disagreement is worth.</summary>
public sealed record Reconciliation
{
    public required IReadOnlyList<EventComparison> Comparisons { get; init; }

    /// <summary>The statement as the document tells it.</summary>
    public required LaytimeStatement FromStatementOfFacts { get; init; }

    /// <summary>The statement as AIS tells it.</summary>
    public required LaytimeStatement FromAis { get; init; }

    public IEnumerable<EventComparison> Disagreements =>
        Comparisons.Where(c => c.Verdict == ComparisonVerdict.Disagrees);

    public IEnumerable<EventComparison> Uncorroborated =>
        Comparisons.Where(c => c.Verdict == ComparisonVerdict.AisCannotObserve);

    /// <summary>
    /// What the disagreement is worth: the difference between the two statements' demurrage.
    ///
    /// **Not the sum of the deltas priced individually**, which is the intuitive thing to do and
    /// is wrong. What a discrepancy costs depends entirely on the terms. A two-hour difference in
    /// berthing is worth nothing if commencement is turn-time-driven; it is worth two hours at the
    /// full rate if the vessel was already on demurrage; and it can be worth far more than its own
    /// duration if it pushes the total across the allowance. The only honest price is the
    /// difference between the two calculations (ADR-0031).
    /// </summary>
    public Money DemurrageDifference => FromStatementOfFacts.DemurrageOwed - FromAis.DemurrageOwed;

    public override string ToString()
    {
        var rows = string.Join(Environment.NewLine, Comparisons.Select(c => $"  {c}"));
        var difference = DemurrageDifference;
        var direction = difference.MinorUnits switch
        {
            > 0 => "the document claims MORE than AIS supports",
            < 0 => "the document claims LESS than AIS supports",
            _ => "the two accounts price identically",
        };

        return $"""
            {rows}

              demurrage on the document's timeline : {FromStatementOfFacts.DemurrageOwed}
              demurrage on the AIS timeline        : {FromAis.DemurrageOwed}
              difference                           : {difference}  ({direction})
            """;
    }
}

/// <summary>
/// Compares a Statement of Facts against what AIS observed, and prices the difference.
///
/// Records the disagreement rather than resolving it. That is the same position rule R10 takes
/// when a vessel's own navigational status contradicts its own speed: two sources that should
/// agree, compared, with neither declared the winner. Here the stakes are simply higher, because
/// one of the accounts is signed by three parties and the other is a transponder.
/// </summary>
public sealed class TimelineReconciler
{
    private readonly ReconciliationTolerances _tolerances;
    private readonly LaytimeCalculator _calculator = new();

    public TimelineReconciler(ReconciliationTolerances? tolerances = null) =>
        _tolerances = tolerances ?? ReconciliationTolerances.Default;

    public Reconciliation Reconcile(StatementOfFacts sof, PortCall portCall, CharterPartyTerms terms)
    {
        var timeline = VoyageTimeline.FromPortCall(portCall)
            ?? throw new ArgumentException(
                "the port call has no berth phase, so there are no cargo operations to reconcile",
                nameof(portCall));

        if (!timeline.BerthSpanIsTrustworthy)
        {
            throw new ArgumentException(
                $"{timeline.UntrustworthyHoursInBerthSpan:F2}h inside the berth span have geometry " +
                "the pipeline does not stand behind, so it cannot be compared against a document",
                nameof(portCall));
        }

        var anchorage = portCall.Phases
            .Where(p => p.Phase == StopPhase.Anchorage)
            .Select(p => p.Stop)
            .ToList();

        var comparisons = new List<EventComparison>
        {
            Compare(sof, SofEventKind.Anchored,
                anchorage.Count > 0 ? anchorage[0].StartedUtc : null,
                TimeSpan.Zero, _tolerances.PhysicalEventTolerance),

            Compare(sof, SofEventKind.AnchorAweigh,
                anchorage.Count > 0 ? anchorage[^1].EndedUtc : null,
                TimeSpan.Zero, _tolerances.PhysicalEventTolerance),

            // The one with a real expected lag: AIS sees the vessel stop, the document records it
            // secured, and those are 30-60 minutes apart even when both are right.
            Compare(sof, SofEventKind.AllFast,
                timeline.BerthedUtc,
                _tolerances.AllFastLagAfterAisStop, _tolerances.AllFastTolerance),

            Compare(sof, SofEventKind.LeftBerth,
                timeline.DepartedBerthUtc,
                TimeSpan.Zero, _tolerances.PhysicalEventTolerance),
        };

        // Everything AIS is structurally unable to see. Listed rather than omitted: silence must
        // not read as agreement.
        foreach (var kind in new[]
        {
            SofEventKind.NoticeOfReadinessTendered,
            SofEventKind.CargoCommenced,
            SofEventKind.CargoCompleted,
        })
        {
            var e = sof.First(kind);
            comparisons.Add(new EventComparison(
                kind, e?.Label, e?.TimestampUtc, null, TimeSpan.Zero,
                e is null ? ComparisonVerdict.AbsentFromStatement : ComparisonVerdict.AisCannotObserve));
        }

        return new Reconciliation
        {
            Comparisons = comparisons,
            FromStatementOfFacts = StatementFrom(sof, terms),
            FromAis = _calculator.Calculate(terms, timeline.BerthedUtc, timeline.DepartedBerthUtc!.Value),
        };
    }

    /// <summary>
    /// The laytime statement the document itself supports: its own berthing and completion times,
    /// and its own suspensions as excepted periods.
    /// </summary>
    private LaytimeStatement StatementFrom(StatementOfFacts sof, CharterPartyTerms terms)
    {
        var berthed = sof.First(SofEventKind.AllFast)?.TimestampUtc;
        var completed = sof.Last(SofEventKind.LeftBerth)?.TimestampUtc
            ?? sof.Last(SofEventKind.CargoCompleted)?.TimestampUtc
            ?? throw new ArgumentException(
                "the statement records neither a departure nor a cargo completion, so it defines "
                + "no end to laytime", nameof(sof));

        var exceptions = sof.SuspensionPeriods()
            .Select(p => new LaytimeException(
                p.Suspended.TimestampUtc, p.Resumed.TimestampUtc,
                p.Suspended.Remark ?? p.Suspended.Label))
            .ToList();

        return _calculator.Calculate(terms with { Exceptions = exceptions }, berthed, completed);
    }

    private EventComparison Compare(
        StatementOfFacts sof,
        SofEventKind kind,
        DateTime? aisUtc,
        TimeSpan expectedOffset,
        TimeSpan tolerance)
    {
        var e = sof.First(kind);

        if (e is null)
        {
            return new EventComparison(kind, null, null, aisUtc, expectedOffset,
                ComparisonVerdict.AbsentFromStatement);
        }

        if (aisUtc is null)
        {
            return new EventComparison(kind, e.Label, e.TimestampUtc, null, expectedOffset,
                ComparisonVerdict.AbsentFromAis);
        }

        var unexplained = e.TimestampUtc - aisUtc.Value - expectedOffset;

        return new EventComparison(kind, e.Label, e.TimestampUtc, aisUtc, expectedOffset,
            unexplained.Duration() <= tolerance
                ? ComparisonVerdict.Agrees
                : ComparisonVerdict.Disagrees);
    }
}
