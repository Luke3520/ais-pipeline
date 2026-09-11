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

    /// <summary>Meaningful only when <see cref="IsComplete"/> (ADR-0011).</summary>
    public double DurationHours { get; init; }

    public double CentroidLatitude { get; init; }
    public double CentroidLongitude { get; init; }

    /// <summary>Meaningful only when <see cref="GeometryTrustworthy"/> (ADR-0025).</summary>
    public double MaxDriftNm { get; init; }

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

/// <summary>How often a rule refused something, for the quality report.</summary>
public sealed record RuleHitCount
{
    public string RuleId { get; init; } = "";
    public long Quarantined { get; init; }
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
