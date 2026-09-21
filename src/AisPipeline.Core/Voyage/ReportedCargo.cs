using AisPipeline.Core.Domain;

namespace AisPipeline.Core.Voyage;

/// <summary>
/// What reported draught says about cargo, and whether it agrees with the geometry.
///
/// The pipeline decides that a vessel worked alongside from drift geometry alone -- how far it
/// moved, normalised by how long it stayed. Reported draught is a second signal for the same
/// event, arriving by a completely different route, and the useful question is whether the two
/// agree more often than chance.
///
/// They are not independent in the way a reader might assume, and the page says so. Position is
/// measured by a receiver; draught is typed by a person, in the same message as the destination
/// and the ETA. So this corroborates the berth inference exactly as far as crews bother to update
/// a field, which is a real limit and not a small one (ADR-0048).
/// </summary>
public sealed record ReportedCargo
{
    /// <summary>Complete calls reporting a draught at both ends -- the denominator.</summary>
    public required int CallsWithDraughtAtBothEnds { get; init; }

    /// <summary>Complete calls that did not report one at both ends, so nothing is claimed.</summary>
    public required int CallsWithoutDraught { get; init; }

    public required int Discharged { get; init; }
    public required int Loaded { get; init; }
    public required int Unchanged { get; init; }

    /// <summary>Calls the drift geometry put alongside, and how many of those reported a change.</summary>
    public required int BerthedCalls { get; init; }
    public required int BerthedWithAChange { get; init; }

    /// <summary>Calls the geometry saw only at anchor, and how many reported a change anyway.</summary>
    public required int AnchorageOnlyCalls { get; init; }
    public required int AnchorageOnlyWithAChange { get; init; }

    public double? BerthedSharePercent => Share(BerthedWithAChange, BerthedCalls);

    public double? AnchorageOnlySharePercent => Share(AnchorageOnlyWithAChange, AnchorageOnlyCalls);

    /// <summary>
    /// How much likelier a berthed call is to report a cargo change. Null when either sample is
    /// empty or the anchorage share is zero -- a ratio against nothing is not a ratio.
    /// </summary>
    public double? TimesLikelierWhenBerthed =>
        BerthedSharePercent is { } berthed && AnchorageOnlySharePercent is > 0 and { } anchorage
            ? berthed / anchorage
            : null;

    /// <summary>Every call examined is accounted for by exactly one outcome.</summary>
    public bool IsBalanced =>
        Discharged + Loaded + Unchanged == CallsWithDraughtAtBothEnds;

    private static double? Share(int part, int whole) => whole == 0 ? null : 100.0 * part / whole;
}

/// <summary>Counts the movements and the agreement. Pure.</summary>
public static class ReportedCargoBuilder
{
    public static ReportedCargo Build(IReadOnlyList<PortCall> calls)
    {
        // Complete calls only. An open call's ends are not its ends, so a draught "change" across
        // them could be a vessel that simply sailed out of the window mid-operation.
        var complete = calls.Where(c => c.IsComplete).ToList();
        var known = complete.Where(c => c.ReportedCargoMovement != CargoMovement.Unknown).ToList();

        var moved = (PortCall c) =>
            c.ReportedCargoMovement is CargoMovement.Loaded or CargoMovement.Discharged;

        // "Worked alongside" as the geometry sees it: at least one phase classified as a berth.
        var berthed = known.Where(c => c.WorkingHours > 0).ToList();
        var anchorageOnly = known.Where(c => c.WorkingHours <= 0).ToList();

        return new ReportedCargo
        {
            CallsWithDraughtAtBothEnds = known.Count,
            CallsWithoutDraught = complete.Count - known.Count,
            Discharged = known.Count(c => c.ReportedCargoMovement == CargoMovement.Discharged),
            Loaded = known.Count(c => c.ReportedCargoMovement == CargoMovement.Loaded),
            Unchanged = known.Count(c => c.ReportedCargoMovement == CargoMovement.Unchanged),
            BerthedCalls = berthed.Count,
            BerthedWithAChange = berthed.Count(moved),
            AnchorageOnlyCalls = anchorageOnly.Count,
            AnchorageOnlyWithAChange = anchorageOnly.Count(moved),
        };
    }
}
