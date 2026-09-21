using AisPipeline.Core.Voyage;
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

    /// <summary>
    /// One port call by id, or null.
    ///
    /// A dedicated lookup rather than filtering a page of <see cref="ListPortCalls"/>: that one
    /// ranks by total duration before applying its limit, so a call outside the page is invisible
    /// and a caller cannot tell "no such call" from "not on this page" (ADR-0028).
    /// </summary>
    StoredPortCall? GetPortCall(long id);

    /// <summary>
    /// One vessel's port calls that share time with a window, most recent first.
    ///
    /// Exists so a Statement of Facts can be matched to the call it describes rather than to
    /// whichever call <see cref="MostRecentCompletePortCall"/> happens to return. Overlap is
    /// strict: a call ending exactly as the window opens shares no time with it (ADR-0035).
    ///
    /// Incomplete calls are included deliberately. Excluding them would report "no call matches
    /// this document" for a document whose call the store does hold but could not measure, and the
    /// trust gates downstream already refuse with a message that says which problem it is.
    /// </summary>
    IReadOnlyList<StoredPortCall> PortCallsOverlapping(long mmsi, DateTime fromUtc, DateTime toUtc);

    /// <summary>
    /// Every attributed port call's hours, unfiltered.
    ///
    /// Unfiltered on purpose: deciding which calls a benchmark may use is a judgement about
    /// evidence, and it belongs where it can be tested and where the exclusions can be counted,
    /// not in a WHERE clause nobody sees (ADR-0043).
    /// </summary>
    IReadOnlyList<PortCallHours> PortCallHoursForBenchmarks();

    /// <summary>
    /// Every ETA in the store, binned by whole days ahead of the fix that carried it.
    ///
    /// Counted in SQL rather than streamed into Core: this is an aggregate over five million rows
    /// and the answer is a few hundred bins. Negative days are kept -- an ETA already in the past
    /// is the population the rollover is supposed to make impossible.
    /// </summary>
    IReadOnlyList<EtaHorizonBin> EtaHorizon();

    /// <summary>Every distinct string typed into the destination field, with how far it spread.</summary>
    IReadOnlyList<DestinationCount> TypedDestinations();

    /// <summary>How many vessels typed a destination at all, and how many ever changed it.</summary>
    DestinationTypists DestinationTypists();

    IReadOnlyList<RuleHitCount> QualityReport();

    /// <summary>
    /// How many stops carry a status that contradicts their own speed, and how many there are.
    ///
    /// Separate from <see cref="QualityReport"/> because R10 is recorded as a column on a derived
    /// record, not as a quarantine row or a flag — so `ais quality` can report it without the
    /// report pretending it counts the same thing as the others.
    /// </summary>
    StopStatusDisagreement StatusDisagreement();

    /// <summary>
    /// Per vessel, how its status behaved at every stop: lapses, total stops, and hours.
    ///
    /// Every vessel with at least one stop, including those that never lapsed. Ranking by rate
    /// needs the denominator, and counting the vessels that always got it right needs the rows a
    /// lapses-only query would discard.
    /// </summary>
    IReadOnlyList<VesselStopStatus> StopStatusByVessel();

    /// <summary>
    /// Per vessel, fixes flagged R12 against total fixes. Only vessels that lapsed: unlike stops,
    /// the no-lapse population here is every other vessel in the feed and says nothing.
    ///
    /// Scans <c>position_report</c>, so it is an export-time query rather than a request-time one.
    /// </summary>
    IReadOnlyList<VesselFixStatus> FixStatusByVessel();

    IReadOnlyList<StoredRun> ListRuns();
}
