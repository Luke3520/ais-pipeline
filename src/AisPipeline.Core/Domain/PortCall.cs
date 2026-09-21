using AisPipeline.Core.Geo;

namespace AisPipeline.Core.Domain;

/// <summary>Whether a stop looks like lying alongside or lying at anchor.</summary>
public enum StopPhase
{
    /// <summary>Drift below the berth threshold: held in place by mooring lines.</summary>
    Berth,

    /// <summary>Drift above it: swinging on an anchor chain.</summary>
    Anchorage,

    /// <summary>
    /// Too few reliable fixes for the drift to mean anything, so the question is unanswered.
    ///
    /// Recorded rather than guessed. Counting these as a berth is how the collapsed-geometry
    /// defect produced its most confident wrong answers, and counting them as an anchorage
    /// would be the same mistake pointed the other way.
    /// </summary>
    Unknown,
}

/// <summary>One stop within a port call, in order.</summary>
public sealed record PortCallPhase(int Sequence, StopPhase Phase, StopEvent Stop);

/// <summary>What the vessel's own reported draught says happened to its cargo.</summary>
public enum CargoMovement
{
    /// <summary>No draught was reported at either end, so nothing can be said.</summary>
    Unknown,

    /// <summary>Reported draught did not move by more than the threshold.</summary>
    Unchanged,

    /// <summary>Reported draught fell: cargo off.</summary>
    Discharged,

    /// <summary>Reported draught rose: cargo on.</summary>
    Loaded,
}

/// <summary>How much reported draught must move before it is read as cargo.</summary>
public static class CargoThresholds
{
    /// <summary>
    /// Metres of reported draught change below which nothing is claimed. Half a metre.
    ///
    /// Draught is typed in, and crews round it -- 12.0, 12.5, 13.0 are far commoner than 12.3.
    /// A threshold under half a metre would turn a crew's rounding into a cargo operation. It is
    /// a heuristic and ADR-0048 says so; it was chosen from the reporting granularity, not tuned
    /// against the result.
    /// </summary>
    public const double ReportedChangeM = 0.5;
}

/// <summary>
/// A vessel's consecutive nearby stops chained into one visit.
///
/// Reaches waiting-versus-working time without a port boundary dataset: the split comes from
/// drift geometry and timestamps alone. Waiting at anchor and working alongside are the two
/// quantities a laytime calculation is built from.
/// </summary>
public sealed record PortCall
{
    public required long Mmsi { get; init; }
    public required IReadOnlyList<PortCallPhase> Phases { get; init; }

    /// <summary>
    /// The nearest port to this call's centroid, with the distance to it, or null when nothing was
    /// close enough to name -- or when no gazetteer was loaded.
    ///
    /// Null carries two meanings and that is deliberate: both say "this pipeline is not telling you
    /// which port this was", which is the only claim either state supports. Distinguishing them
    /// would invite a caller to treat "no gazetteer" as "at sea".
    /// </summary>
    public PortAttribution? Attribution { get; init; }

    public DateTime ArrivedUtc => Phases[0].Stop.StartedUtc;

    /// <summary>First draught reported anywhere in this call, or null when none was.</summary>
    public double? DraughtOnArrivalM => Phases
        .Select(p => p.Stop.DraughtFirstM)
        .FirstOrDefault(d => d is not null);

    /// <summary>Last draught reported anywhere in this call, or null when none was.</summary>
    public double? DraughtOnDepartureM => Phases
        .Select(p => p.Stop.DraughtLastM)
        .LastOrDefault(d => d is not null);

    /// <summary>
    /// What the reported draught says the cargo did.
    ///
    /// A <em>report</em>, not an observation. Draught arrives in the same hand-typed voyage message
    /// as the destination and the ETA, so this says a crew told the world its draught changed --
    /// which is evidence about cargo exactly as far as the crew is reliable, and no further
    /// (ADR-0048). It is deliberately not folded into waiting or working hours, which are derived
    /// from measured position alone.
    /// </summary>
    public CargoMovement ReportedCargoMovement
    {
        get
        {
            if (DraughtOnArrivalM is not { } arrival || DraughtOnDepartureM is not { } departure)
            {
                return CargoMovement.Unknown;
            }

            var change = departure - arrival;

            if (Math.Abs(change) <= CargoThresholds.ReportedChangeM)
            {
                return CargoMovement.Unchanged;
            }

            return change > 0 ? CargoMovement.Loaded : CargoMovement.Discharged;
        }
    }

    public DateTime DepartedUtc => Phases[^1].Stop.EndedUtc;

    /// <summary>Hours spent at anchor. Meaningful only when <see cref="IsComplete"/>.</summary>
    public double WaitingHours => Phases
        .Where(p => p.Phase == StopPhase.Anchorage)
        .Sum(p => p.Stop.DurationHours);

    /// <summary>Hours spent alongside. Meaningful only when <see cref="IsComplete"/>.</summary>
    public double WorkingHours => Phases
        .Where(p => p.Phase == StopPhase.Berth)
        .Sum(p => p.Stop.DurationHours);

    /// <summary>
    /// Hours in phases whose geometry could not be trusted, counted as neither waiting nor
    /// working. Surfaced rather than absorbed: silently folding it into either figure would put
    /// a number the data does not support into a laytime calculation.
    /// </summary>
    public double UnclassifiedHours => Phases
        .Where(p => p.Phase == StopPhase.Unknown)
        .Sum(p => p.Stop.DurationHours);

    public double CentroidLatitude => Phases.Average(p => p.Stop.CentroidLatitude);

    public double CentroidLongitude => Phases.Average(p => p.Stop.CentroidLongitude);

    /// <summary>A port call is only as complete as its least complete stop.</summary>
    public bool IsComplete => Phases.All(p => p.Stop.IsComplete);
}
