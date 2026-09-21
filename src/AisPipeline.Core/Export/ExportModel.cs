using AisPipeline.Core.Benchmarks;
using AisPipeline.Core.Laytime;
using AisPipeline.Core.Quality;
using AisPipeline.Core.Voyage;
using AisPipeline.Core.Query;

namespace AisPipeline.Core.Export;

/// <summary>
/// Where an export came from, carried in the export itself.
///
/// Rule 1 applied to a published artefact: a figure on a website is as untraceable as a row in a
/// table unless it says which ingest runs produced it. A site built from this can state its own
/// provenance instead of asking the reader to trust it (ADR-0039).
/// </summary>
public sealed record ExportManifest
{
    public required DateTime GeneratedUtc { get; init; }

    /// <summary>
    /// The source files this export summarises, distinct, in the order they were first ingested.
    ///
    /// Distinct because a file can be ingested more than once -- re-running the refresh on a file
    /// already in the store is a no-op for the data but still records a run, by design. Listing
    /// runs here put the same day in twice and made the site claim it was built from eight files
    /// when seven exist.
    /// </summary>
    public required IReadOnlyList<string> SourceFiles { get; init; }

    public required DateTime FirstFixUtc { get; init; }
    public required DateTime LastFixUtc { get; init; }
    /// <summary>
    /// Rows the source files contained, counting each file once.
    ///
    /// A statement about the data, not about the work: summing every run double-counts a file that
    /// was ingested twice, and the site phrases this as the size of the feed. Re-reading the same
    /// 22 million rows does not make the feed larger.
    /// </summary>
    public required long RowsRead { get; init; }
    public required long RowsStored { get; init; }
}

/// <summary>The figures a landing page leads with.</summary>
public sealed record ExportSummary
{
    /// <summary>
    /// Vessels that stopped at least once — the denominator the lapse figures are drawn from.
    ///
    /// Not the number of vessels in the feed, and not the number in this document. An earlier
    /// version of this field was simply "Vessels" and held the count of offenders, which on a
    /// landing page reads as the size of the dataset. A figure whose name does not say what it
    /// counts is the defect this project exists to avoid.
    /// </summary>
    public required long VesselsWithStops { get; init; }

    /// <summary>Vessels with at least one lapse in either direction — the rows in this document.</summary>
    public required long VesselsWithLapses { get; init; }

    public required long Stops { get; init; }
    public required long PortCalls { get; init; }

    /// <summary>Stops where the status claimed under way while the vessel sat still (R10).</summary>
    public required long ArrivalLapses { get; init; }

    /// <summary>Fixes claiming a stationary status while moving (R12).</summary>
    public required long DepartureLapses { get; init; }

    public required double HoursClaimingUnderWay { get; init; }

    /// <summary>Vessels that never once updated the dial, over at least <see cref="HabitMinimumStops"/> stops.</summary>
    public required long VesselsAlwaysWrong { get; init; }

    /// <summary>Vessels that never once got it wrong, over the same minimum.</summary>
    public required long VesselsAlwaysRight { get; init; }

    /// <summary>
    /// How many stops a vessel needs before its record counts as a habit rather than an accident.
    ///
    /// Three. A vessel with one stop is at 0% or 100% and neither means anything; the interesting
    /// claim is that a crew behaves the same way every time, and that needs repetitions to be a
    /// claim at all. Low enough to keep most vessels in scope, and stated rather than tuned.
    /// </summary>
    public const int HabitMinimumStops = 3;
}

/// <summary>One vessel, both directions of the same lapse, with the denominators.</summary>
public sealed record VesselRecord
{
    public required long Mmsi { get; init; }

    /// <summary>
    /// The vessel's name as broadcast — itself a hand-typed field, like the status this record is
    /// about. Null when the feed never carried one.
    /// </summary>
    public string? Name { get; init; }

    public string? ShipType { get; init; }

    public long ArrivalLapses { get; init; }
    public long TotalStops { get; init; }
    public double HoursClaimingUnderWay { get; init; }

    public long DepartureLapses { get; init; }
    public long TotalFixes { get; init; }
    public double FastestKnWhileClaimingStationary { get; init; }

    public double ArrivalSharePercent => TotalStops == 0 ? 0 : 100.0 * ArrivalLapses / TotalStops;

    public double DepartureSharePercent => TotalFixes == 0 ? 0 : 100.0 * DepartureLapses / TotalFixes;

    /// <summary>Forgot in both directions — the strongest single finding about a vessel.</summary>
    public bool BothDirections => ArrivalLapses > 0 && DepartureLapses > 0;

    /// <summary>Never once updated the dial across a meaningful number of stops.</summary>
    public bool NeverUpdated =>
        TotalStops >= ExportSummary.HabitMinimumStops && ArrivalLapses == TotalStops;
}

/// <summary>Everything a static site needs, in one document.</summary>
public sealed record ExportDocument
{
    public required ExportManifest Manifest { get; init; }
    public required ExportSummary Summary { get; init; }
    public required IReadOnlyList<QualityReportLine> Rules { get; init; }
    public required StopStatusDisagreement StatusDisagreement { get; init; }
    public required IReadOnlyList<VesselRecord> Vessels { get; init; }

    /// <summary>
    /// How long calls at each port took, with the sample and the exclusions behind every figure.
    ///
    /// Publishable in a way a priced statement is not. A benchmark is a property of a port and
    /// ranks rather than judges (ADR-0043); a demurrage figure names a vessel and a sum of money,
    /// on charter terms supplied at request time, and stays on the operator's own machine
    /// (ADR-0047). Nothing derived from <c>LaytimeStatement</c> belongs in this document.
    /// </summary>
    public required IReadOnlyList<PortBenchmark> Ports { get; init; }

    /// <summary>How many calls AIS can price at all, and the named reason for each one it cannot.</summary>
    public required LaytimePriceability Priceability { get; init; }

    /// <summary>
    /// The thresholds the site prints beside its figures, projected from the constants.
    ///
    /// A page that shows "too few" has to say how few is too few, and a page that excludes a call
    /// has to say from what distance. Copying those numbers into the site would make captions that
    /// can go stale beside live figures -- the defect this project exists to prevent, applied to a
    /// label rather than to a number. The API answers the same question at /meta (ADR-0047).
    /// </summary>
    public required ExportThresholds Thresholds { get; init; }

    /// <summary>
    /// Every ETA in the feed, binned by how far ahead it pointed.
    ///
    /// Publishable without reservation: it is a shape, not a claim about any vessel. It is also the
    /// clearest thing in this document -- the two populations and the empty run between them are
    /// visible at a glance, where the prose describing them takes a paragraph.
    /// </summary>
    public required EtaHorizon EtaHorizon { get; init; }

    /// <summary>
    /// What the crew typed where the destination goes.
    ///
    /// Strings, and counts of them. No vessel is named against any string -- the interesting claim
    /// is about the field, not about who filled it in.
    /// </summary>
    public required TypedDestinations Destinations { get; init; }

    /// <summary>
    /// What reported draught says the cargo did, and whether it agrees with the drift geometry.
    ///
    /// Counts of calls, never a named vessel: "33 of 66 berthed calls reported a change" is a
    /// statement about the feed, where "this tanker discharged" is a statement about somebody's
    /// business.
    /// </summary>
    public required ReportedCargo Cargo { get; init; }
}

/// <summary>The constants a reader needs in order to argue with a figure in this document.</summary>
public sealed record ExportThresholds
{
    /// <summary>Beyond this, a call is nearest a port rather than at it (ADR-0034).</summary>
    public double PlausiblyAtPortNm => Geo.PortAttributionThresholds.PlausiblyAtPortNm;

    /// <summary>Usable calls needed before a median is published at all (ADR-0043).</summary>
    public int MinimumCallsForMedian => BenchmarkMinimums.ForMedian;

    /// <summary>Usable calls needed before a 90th percentile is published at all.</summary>
    public int MinimumCallsForPercentile => BenchmarkMinimums.ForPercentile;

    /// <summary>Stops a vessel needs before its record counts as a habit.</summary>
    public int HabitMinimumStops => ExportSummary.HabitMinimumStops;
}

/// <summary>
/// How many port calls could be priced from AIS alone, and why the rest could not.
///
/// Publishable where a priced statement is not, because it contains no money and no charter party.
/// Whether a call can be priced depends only on its own geometry — a berth phase must exist, and
/// the hours inside it must be trustworthy (<see cref="LaytimeAssessor"/>) — so this is a fact
/// about the evidence, not about anyone's commercial terms.
///
/// It is also the honest half of the pitch. A page claiming AIS prices port calls, without saying
/// that it declines four in five of them, would be selling something the pipeline does not do.
/// </summary>
public sealed record LaytimePriceability
{
    /// <summary>Port calls examined — the denominator.</summary>
    public required int CallsAssessed { get; init; }

    /// <summary>Calls a laytime statement could be produced for.</summary>
    public required int Priceable { get; init; }

    /// <summary>Refused: no berth phase, so there were no cargo operations to measure.</summary>
    public required int NoBerthPhase { get; init; }

    /// <summary>Refused: hours inside the berth span have geometry the pipeline will not stand behind.</summary>
    public required int BerthGeometryUntrustworthy { get; init; }

    /// <summary>
    /// Every call is accounted for by exactly one outcome.
    ///
    /// The same identity <c>IngestCounters</c> applies to rows and <c>PortBenchmark</c> applies to
    /// its exclusions. A tally that does not add up is hiding a fourth outcome nobody named.
    /// </summary>
    public bool IsBalanced =>
        Priceable + NoBerthPhase + BerthGeometryUntrustworthy == CallsAssessed;
}
