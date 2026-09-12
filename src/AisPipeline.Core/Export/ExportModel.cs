using AisPipeline.Core.Quality;
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

    /// <summary>The source files whose rows this export summarises, in ingest order.</summary>
    public required IReadOnlyList<string> SourceFiles { get; init; }

    public required DateTime FirstFixUtc { get; init; }
    public required DateTime LastFixUtc { get; init; }
    public required long RowsRead { get; init; }
    public required long RowsStored { get; init; }
}

/// <summary>The figures a landing page leads with.</summary>
public sealed record ExportSummary
{
    public required long Vessels { get; init; }
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
}
