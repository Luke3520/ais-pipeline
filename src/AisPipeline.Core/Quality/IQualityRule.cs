using AisPipeline.Core.Domain;
using AisPipeline.Core.Parsing;

namespace AisPipeline.Core.Quality;

/// <summary>Common identity for every rule, so the quality report can enumerate them.</summary>
public interface IQualityRule
{
    /// <summary>Stable id, e.g. "R4".</summary>
    string Id { get; }

    /// <summary>One line, shown in the quality report.</summary>
    string Description { get; }
}

/// <summary>
/// A rule that judges a line before it can be parsed into a record -- field count, malformed
/// values. Only R1 lives here.
/// </summary>
public interface ILineRule : IQualityRule
{
    RuleHit? Evaluate(AisSourceLine line, ParseResult parse);
}

/// <summary>
/// A rule that judges a single parsed row on its own, with no reference to any other row.
///
/// Rules needing a vessel's fixes in time order (teleport, coverage gap, speed consistency,
/// same-second conflicts) are NOT these. They run as a post-ingest annotate pass reading from
/// the store in (mmsi, ts_utc) order -- see docs/rules/quality-rules.md.
/// </summary>
public interface IRecordRule : IQualityRule
{
    RuleHit? Evaluate(RawAisRecord record);
}
