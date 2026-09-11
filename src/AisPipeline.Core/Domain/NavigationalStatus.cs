namespace AisPipeline.Core.Domain;

/// <summary>What a vessel's self-reported navigational status claims it was doing.</summary>
public enum ReportedActivity
{
    /// <summary>Status not reported, or a value carrying no claim either way.</summary>
    Unknown,

    /// <summary>The vessel claims to be making way.</summary>
    UnderWay,

    /// <summary>The vessel claims to be stationary -- moored, anchored, or aground.</summary>
    Stationary,
}

/// <summary>
/// Maps the AIS navigational status strings the DMA feed emits onto the only question rule R10
/// asks of them: was the vessel claiming to move, or claiming to be still?
///
/// Mapping every status matters. Tanker data carries "Constrained by her draught" on 18,203
/// sampled rows -- a status an under-way/at-rest binary would not anticipate, and one that is
/// unambiguously a vessel under way.
/// </summary>
public static class NavigationalStatus
{
    public static ReportedActivity Classify(string? status) => status switch
    {
        null or "" => ReportedActivity.Unknown,

        "Under way using engine" => ReportedActivity.UnderWay,
        "Under way sailing" => ReportedActivity.UnderWay,
        "Constrained by her draught" => ReportedActivity.UnderWay,
        "Engaged in fishing" => ReportedActivity.UnderWay,
        "Restricted maneuverability" => ReportedActivity.UnderWay,
        "Reserved for future use [9]" => ReportedActivity.Unknown,

        "At anchor" => ReportedActivity.Stationary,
        "Moored" => ReportedActivity.Stationary,
        "Aground" => ReportedActivity.Stationary,

        // "Not under command" means unable to manoeuvre, which says nothing about whether the
        // vessel is making way -- a broken-down ship still drifts. No claim either way.
        "Not under command" => ReportedActivity.Unknown,

        // "Unknown value" is DMA's own placeholder and appears on 21% of feed rows.
        "Unknown value" => ReportedActivity.Unknown,

        _ => ReportedActivity.Unknown,
    };
}
