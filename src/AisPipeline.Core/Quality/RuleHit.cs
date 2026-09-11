namespace AisPipeline.Core.Quality;

/// <summary>
/// What a rule does to a row. There is no third option: a row that leaves the pipeline
/// without a quarantine record, a flag, or a counter is a silent loss
/// (docs/rules/checks-and-review.md, blocking class 1).
/// </summary>
public enum RuleAction
{
    /// <summary>Row does not reach position_report. Quarantined with its raw text as evidence.</summary>
    Reject,

    /// <summary>Row is stored, with the rule id recorded in quality_flags alongside it.</summary>
    Flag,
}

/// <summary>A rule firing on one row, with the detail that explains why.</summary>
/// <param name="RuleId">Stable id, e.g. "R4". Ids are never reused, including for retired rules.</param>
/// <param name="Action">Reject or flag.</param>
/// <param name="Detail">Human-readable reason, stored with the quarantine row.</param>
public sealed record RuleHit(string RuleId, RuleAction Action, string Detail);
