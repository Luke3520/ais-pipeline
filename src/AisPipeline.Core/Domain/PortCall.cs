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

    public DateTime ArrivedUtc => Phases[0].Stop.StartedUtc;

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
