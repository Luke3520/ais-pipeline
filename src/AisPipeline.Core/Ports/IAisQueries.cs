using AisPipeline.Core.Domain;
using AisPipeline.Core.Query;

namespace AisPipeline.Core.Ports;

/// <summary>
/// The read side.
///
/// Separate from <see cref="IAisStore"/> because the two have genuinely different shapes: the
/// write port exists to get rows in with their provenance intact, and this one exists to answer
/// questions. Growing query methods onto the write port would make every adapter implement both
/// concerns, and would blur which methods the ingest path is allowed to call.
///
/// This is read/write separation without ceremony -- one database, two interfaces. It is not
/// CQRS and the README does not call it that (ADR-0027).
/// </summary>
public interface IAisQueries : IDisposable
{
    StoredVessel? GetVessel(long mmsi);

    /// <summary>
    /// Every vessel in one query.
    ///
    /// The batched shape exists for DataLoader. Resolving a page of port calls through a
    /// per-call <see cref="GetVessel"/> is a textbook N+1 -- 100 calls, 100 round trips -- and
    /// this is what makes the batched path possible rather than merely intended (ADR-0027).
    /// Callers get back only the vessels that exist; a missing mmsi is simply absent.
    /// </summary>
    IReadOnlyList<StoredVessel> GetVessels(IReadOnlyCollection<long> mmsis);

    IReadOnlyList<StoredVessel> ListVessels(string? shipType, int limit);

    IReadOnlyList<StoredStop> ListStops(StopFilter filter);

    /// <summary>Stops for several vessels at once. The batched shape, again for DataLoader.</summary>
    IReadOnlyList<StoredStop> GetStopsForVessels(IReadOnlyCollection<long> mmsis, int limitPerVessel);

    IReadOnlyList<StoredPortCall> ListPortCalls(PortCallFilter filter);

    /// <summary>
    /// A vessel's most recent complete port call, or null if it has none.
    ///
    /// A dedicated query rather than sorting a page of <see cref="ListPortCalls"/>: that one ranks
    /// by total duration before applying its limit, so once a vessel has more calls than the page
    /// holds, the genuinely most recent one can be absent from the page entirely -- and a caller
    /// sorting what survived would report a stale, longer call as the current one.
    /// </summary>
    StoredPortCall? MostRecentCompletePortCall(long mmsi);

    /// <summary>
    /// Port calls for several vessels, most recent first, capped per vessel.
    ///
    /// The cap is a bound on work, not a statement about the vessel. Pair it with
    /// <see cref="CountPortCallsForVessels"/> so a caller can tell a vessel that made exactly the
    /// cap's worth of calls from one whose list was cut short -- otherwise a client summing
    /// waiting and working hours silently undercounts (ADR-0028).
    /// </summary>
    IReadOnlyList<StoredPortCall> GetPortCallsForVessels(IReadOnlyCollection<long> mmsis, int limitPerVessel);

    /// <summary>How many port calls each vessel actually has, uncapped.</summary>
    IReadOnlyDictionary<long, long> CountPortCallsForVessels(IReadOnlyCollection<long> mmsis);

    /// <summary>Phases of several port calls at once, with their stops already joined.</summary>
    IReadOnlyList<(StoredPhase Phase, StoredStop Stop)> GetPhasesForPortCalls(IReadOnlyCollection<long> portCallIds);

    /// <summary>A window of a vessel's raw fixes, for drilling into a stop.</summary>
    IReadOnlyList<PositionFix> ListFixes(long mmsi, DateTime fromUtc, DateTime toUtc, int limit);

    IReadOnlyList<RuleHitCount> QualityReport();

    /// <summary>
    /// How many stops carry a status that contradicts their own speed, and how many there are.
    ///
    /// Separate from <see cref="QualityReport"/> because R10 is recorded as a column on a derived
    /// record, not as a quarantine row or a flag — so `ais quality` can report it without the
    /// report pretending it counts the same thing as the others.
    /// </summary>
    StopStatusDisagreement StatusDisagreement();

    IReadOnlyList<StoredRun> ListRuns();
}
