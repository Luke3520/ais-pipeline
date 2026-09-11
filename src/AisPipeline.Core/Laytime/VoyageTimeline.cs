using AisPipeline.Core.Domain;

namespace AisPipeline.Core.Laytime;

/// <summary>
/// The timestamps a laytime calculation needs, and where each one can come from.
///
/// This is the boundary the whole project has been building toward, and the honest thing to say
/// about it is what AIS **cannot** supply:
///
/// <list type="bullet">
/// <item><b>Notice of Readiness</b> is an email, not a physical event. No transponder emits it.</item>
/// <item><b>Hoses on and off</b> — a tanker sits alongside for hours before pumping starts, and
/// AIS cannot see a manifold.</item>
/// <item><b>Free pratique, customs, exceptions</b> — documents.</item>
/// </list>
///
/// What AIS does supply, and supplies well, is where the vessel physically was and when it stopped
/// moving. That is exactly the half a Statement of Facts is least able to prove and most often
/// disputed about.
/// </summary>
/// <param name="ArrivedUtc">First stop of the call — a proxy for arrival at port limits.</param>
/// <param name="BerthedUtc">Start of the first berth phase, or null if the vessel never berthed.</param>
/// <param name="DepartedBerthUtc">End of the last berth phase.</param>
/// <param name="WaitingHours">Hours at anchor before working.</param>
/// <param name="WorkingHours">Hours alongside.</param>
/// <param name="UntrustworthyHoursInBerthSpan">
/// Hours inside the berth span belonging to phases whose geometry could not be trusted.
///
/// Laytime prices the whole span from first berth to last, so these hours are inside the figure
/// whether or not anyone noticed. ADR-0025 keeps them out of waiting and working precisely because
/// folding them into either "would put a number the data does not support into a laytime
/// calculation" -- and a laytime calculation is exactly what this feeds.
/// </param>
public sealed record VoyageTimeline(
    DateTime ArrivedUtc,
    DateTime? BerthedUtc,
    DateTime? DepartedBerthUtc,
    double WaitingHours,
    double WorkingHours,
    double UntrustworthyHoursInBerthSpan)
{
    /// <summary>
    /// True when every hour between first and last berth is geometry the pipeline stands behind.
    ///
    /// When false, the span cannot be priced: we do not know whether the vessel was alongside for
    /// those hours, and neither excluding them (which favours the charterer) nor counting them
    /// (which favours the owner) is supportable. The honest output is a refusal with the hours
    /// named, so a human decides.
    /// </summary>
    public bool BerthSpanIsTrustworthy => UntrustworthyHoursInBerthSpan <= 0.0;

    /// <summary>
    /// Derive what can be derived from a detected port call.
    ///
    /// Returns null when the call has no berth phase at all: a vessel that only ever lay at anchor
    /// has no cargo operations to measure, and inventing a berth time from an anchorage would put
    /// a fabricated timestamp into a commercial claim.
    /// </summary>
    public static VoyageTimeline? FromPortCall(PortCall portCall)
    {
        var berthPhases = portCall.Phases.Where(p => p.Phase == StopPhase.Berth).ToList();

        if (berthPhases.Count == 0)
        {
            return null;
        }

        var berthedUtc = berthPhases[0].Stop.StartedUtc;
        var departedUtc = berthPhases[^1].Stop.EndedUtc;

        // A call can interleave phases, so an untrusted stop can sit between two berth phases and
        // land inside the span laytime would price.
        var untrustworthy = portCall.Phases
            .Where(p => p.Phase == StopPhase.Unknown)
            .Where(p => p.Stop.StartedUtc < departedUtc && p.Stop.EndedUtc > berthedUtc)
            .Sum(p => (Min(p.Stop.EndedUtc, departedUtc) - Max(p.Stop.StartedUtc, berthedUtc)).TotalHours);

        return new VoyageTimeline(
            ArrivedUtc: portCall.ArrivedUtc,
            BerthedUtc: berthedUtc,
            DepartedBerthUtc: departedUtc,
            WaitingHours: portCall.WaitingHours,
            WorkingHours: portCall.WorkingHours,
            UntrustworthyHoursInBerthSpan: untrustworthy);
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>
    /// The gap between arriving and berthing: time the vessel spent waiting for a berth.
    ///
    /// Whether that time counts against laytime is a question for the charter party, not for AIS.
    /// A "whether in berth or not" clause makes it count; without one it may not. The pipeline
    /// reports the hours and declines to decide.
    /// </summary>
    public double HoursWaitingForBerth =>
        BerthedUtc is { } berthed ? (berthed - ArrivedUtc).TotalHours : 0.0;
}
