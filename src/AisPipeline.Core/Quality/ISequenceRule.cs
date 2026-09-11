using AisPipeline.Core.Domain;

namespace AisPipeline.Core.Quality;

/// <summary>
/// A rule that judges one fix against the one before it for the same vessel.
///
/// These cannot run during ingest. They need a vessel's fixes in time order, which only exists
/// once the rows are stored and can be read back ordered by (mmsi, ts_utc) -- and that ordering
/// is what makes them correct across day boundaries rather than resetting at every file
/// (docs/rules/quality-rules.md).
///
/// The pass that applies them must complete BEFORE detection, because detection excludes
/// flagged fixes from centroid and drift (ADR-0021).
/// </summary>
public interface ISequenceRule : IQualityRule
{
    /// <summary>
    /// Judge <paramref name="current"/> against its predecessor. Returns the hit, or null.
    /// Both fixes belong to the same vessel and <paramref name="previous"/> is the earlier.
    /// </summary>
    RuleHit? Evaluate(PositionFix previous, PositionFix current);
}
