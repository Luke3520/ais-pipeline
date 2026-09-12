namespace AisPipeline.Core.Query;

/// <summary>
/// Records as they exist in the store, carrying the ids a client needs to ask for related things.
///
/// The write-side domain records deliberately have no id -- they describe what was computed, not
/// where it was put -- so the read side has its own shapes rather than bending theirs.
///
/// Declared with init-only properties rather than as positional records, because Dapper binds
/// positional records through the constructor and constructor binding does no type conversion. The
/// providers disagree about what a timestamp and a boolean are (SQLite has neither type), so
/// property binding plus an explicit type handler is what lets one implementation serve both.
/// </summary>
public sealed record StoredVessel
{
    public long Mmsi { get; init; }
    public string? Imo { get; init; }
    public string? Name { get; init; }
    public string? CallSign { get; init; }
    public string? ShipType { get; init; }
    public double? LengthM { get; init; }
    public double? WidthM { get; init; }
    public DateTime FirstSeenUtc { get; init; }
    public DateTime LastSeenUtc { get; init; }
}

public sealed record StoredStop
{
    public long Id { get; init; }
    public long Mmsi { get; init; }
    public DateTime StartedUtc { get; init; }
    public DateTime EndedUtc { get; init; }

    /// <summary>
    /// The observed span between the first and last fix of the stop. Always present, always a
    /// lower bound: when the stop touches a coverage gap or the edge of the window, the vessel was
    /// stationary for at least this long and possibly much longer.
    /// </summary>
    public double ObservedDurationHours { get; init; }

    /// <summary>
    /// The stop's duration, or null when its true extent is unknown (ADR-0011).
    ///
    /// Null rather than a number the caller must remember to qualify. The same convention rule R5
    /// applies to unavailable speed: unknown is not a value, and a consumer summing these cannot
    /// silently treat a censored stop as a measured one. <see cref="ObservedDurationHours"/> keeps
    /// the lower bound available for anyone who wants it deliberately.
    /// </summary>
    public double? DurationHours => IsComplete ? ObservedDurationHours : null;

    public double CentroidLatitude { get; init; }
    public double CentroidLongitude { get; init; }

    /// <summary>The measured drift, always present, meaningful only alongside the flag below.</summary>
    public double ObservedMaxDriftNm { get; init; }

    /// <summary>
    /// How far the vessel wandered, or null when too few fixes survived exclusion for the figure
    /// to mean anything (ADR-0025).
    ///
    /// With one reliable fix the centroid IS that fix, so the drift computes to exactly 0.0 -- the
    /// most confident possible berth reading, on the stops most likely to have been a drifting
    /// vessel. Null makes that unrepresentable as a number.
    /// </summary>
    public double? MaxDriftNm => GeometryTrustworthy ? ObservedMaxDriftNm : null;

    public int FixCount { get; init; }
    public int ReliableFixCount { get; init; }
    public bool GeometryTrustworthy { get; init; }
    public string? ReportedStatus { get; init; }
    public bool StatusAgrees { get; init; }
    public bool IsComplete { get; init; }
    public long FirstPositionId { get; init; }
    public long LastPositionId { get; init; }
}

public sealed record StoredPhase
{
    public long Id { get; init; }
    public long PortCallId { get; init; }
    public long StopId { get; init; }
    public int Sequence { get; init; }
    public string Phase { get; init; } = "";
}

public sealed record StoredPortCall
{
    public long Id { get; init; }
    public long Mmsi { get; init; }
    public DateTime ArrivedUtc { get; init; }
    public DateTime DepartedUtc { get; init; }
    public double WaitingHours { get; init; }
    public double WorkingHours { get; init; }

    /// <summary>Phases whose geometry could not be trusted: neither waiting nor working.</summary>
    public double UnclassifiedHours { get; init; }

    public double CentroidLatitude { get; init; }
    public double CentroidLongitude { get; init; }
    public bool IsComplete { get; init; }

    /// <summary>NGA World Port Index number of the nearest port, or null when none was named.</summary>
    public int? PortWpiNumber { get; init; }

    public string? PortName { get; init; }

    public string? PortCountry { get; init; }

    /// <summary>
    /// How far the call's centroid sat from that port's reference point.
    ///
    /// Returned with the name, always. A World Port Index record is one nominal point near the
    /// harbour entrance, so this runs from 0.11 nm for a berth at Arhus to 3.60 nm for one at
    /// Rostock -- and an offshore anchorage 14 nm out gets a name too. Without the distance, a
    /// client cannot tell those apart, so the name on its own would claim more than the data
    /// supports (ADR-0034).
    /// </summary>
    public double? PortDistanceNm { get; init; }

    /// <summary>
    /// Whether the vessel was plausibly at that port rather than merely nearest to it. Null when
    /// no port was named. A heuristic -- see <see cref="Geo.PortAttributionThresholds"/>.
    /// </summary>
    public bool? PlausiblyAtPort => PortDistanceNm is { } nm
        ? nm <= Geo.PortAttributionThresholds.PlausiblyAtPortNm
        : null;
}

public sealed record StoredRun
{
    public long Id { get; init; }
    public string SourceFile { get; init; } = "";
    public DateTime StartedUtc { get; init; }
    public DateTime? FinishedUtc { get; init; }
    public long RowsRead { get; init; }
    public long RowsFiltered { get; init; }
    public long RowsInserted { get; init; }
    public long RowsDuplicateInFile { get; init; }
    public long RowsDuplicatePriorRun { get; init; }
    public long RowsQuarantined { get; init; }
}

/// <summary>
/// How often a rule fired, split by what firing meant.
///
/// Both halves, because a rule either rejects a row or flags it and there is no third option
/// (ADR-0006). A report carrying only <see cref="Quarantined"/> shows nothing at all for the
/// rules that flag -- R7, R8 and R11 -- so the reader sees a clean feed where the pipeline
/// actually recorded doubt on millions of rows (ADR-0032).
/// </summary>
public sealed record RuleHitCount
{
    public string RuleId { get; init; } = "";

    /// <summary>Rows this rule rejected: one <c>quarantine</c> row each, raw text kept.</summary>
    public long Quarantined { get; init; }

    /// <summary>Rows this rule flagged: kept in <c>position_report</c>, id in quality_flags.</summary>
    public long Flagged { get; init; }

    public long Total => Quarantined + Flagged;
}

/// <summary>
/// How often a vessel's own navigational status contradicted its own speed — rule R10.
///
/// Counted over stops rather than rows, which is why it is its own shape and not a
/// <see cref="RuleHitCount"/>. R10 is not a quarantine-or-flag rule: it implements CLAUDE.md rule 4
/// (record disagreement, do not resolve it) and lives as <c>stop_event.status_agrees</c>, so it
/// appears in neither table the quality report is built from. Reporting it in the same column as
/// R1 or R4 would put a stop count and a row count under one heading.
/// </summary>
public sealed record StopStatusDisagreement
{
    public long TotalStops { get; init; }

    public long Disagreeing { get; init; }

    /// <summary>Percentage of stops carrying the conflict, or null when there are no stops.</summary>
    public double? Share => TotalStops == 0 ? null : 100.0 * Disagreeing / TotalStops;
}

/// <summary>Filters for the collection endpoints. Null means unrestricted.</summary>
public sealed record PortCallFilter
{
    public long? Mmsi { get; init; }

    public double? MinWaitingHours { get; init; }

    /// <summary>
    /// Exclude port calls whose true extent is unknown. Waiting and working hours are only
    /// meaningful for complete calls (ADR-0011), so any caller summing them wants this on.
    /// </summary>
    public bool CompleteOnly { get; init; }

    public int Limit { get; init; } = 100;
}

public sealed record StopFilter
{
    public long? Mmsi { get; init; }

    public double? MinHours { get; init; }

    public bool CompleteOnly { get; init; }

    /// <summary>Only stops where the vessel's own status contradicted its speed (rule R10).</summary>
    public bool DisagreementsOnly { get; init; }

    public int Limit { get; init; } = 100;
}
