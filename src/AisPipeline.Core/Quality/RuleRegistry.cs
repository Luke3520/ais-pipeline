using AisPipeline.Core.Domain;
using AisPipeline.Core.Parsing;
using AisPipeline.Core.Quality.Rules;

namespace AisPipeline.Core.Quality;

/// <summary>
/// The rules the pipeline runs, in order.
///
/// Registered in a list rather than wired by a hand-written switch so that `ais quality` can
/// report every rule without a code change, and adding a rule is a one-file change
/// (docs/rules/quality-rules.md).
/// </summary>
public sealed class RuleRegistry
{
    public RuleRegistry(
        IReadOnlyList<ILineRule> lineRules,
        IReadOnlyList<IRecordRule> recordRules,
        IReadOnlyList<ISequenceRule> sequenceRules)
    {
        LineRules = lineRules;
        RecordRules = recordRules;
        SequenceRules = sequenceRules;
    }

    public IReadOnlyList<ILineRule> LineRules { get; }

    public IReadOnlyList<IRecordRule> RecordRules { get; }

    /// <summary>
    /// The rules the annotate pass runs. Held here rather than listed at the call site so that
    /// the quality report and the pass that applies them cannot drift apart: a rule registered
    /// but never run would report a truthful zero for a check nobody performed.
    /// </summary>
    public IReadOnlyList<ISequenceRule> SequenceRules { get; }

    public IEnumerable<IQualityRule> All =>
        LineRules.Cast<IQualityRule>().Concat(RecordRules).Concat(SequenceRules);

    /// <summary>
    /// Every rule the pipeline knows. <see cref="LineRules"/> and <see cref="RecordRules"/> run
    /// during ingest; <see cref="SequenceRules"/> run later, in the annotate pass.
    /// </summary>
    public static RuleRegistry Default() => new(
        [new R1UnparseableRow()],
        [new R4PositionSentinel(), new R5SpeedUnavailable(), new R6ImplausibleSpeed(),
            new R12StatusClaimsStationary(), new R13EtaImplausiblyFarAhead()],
        [new R7Teleport(), new R8CoverageGap(), new R11SpeedConsistency()]);

    /// <summary>
    /// Judgement for one row: every hit that fired, and whether any of them rejects it.
    /// Evaluation does not stop at the first hit -- a rejected row can still carry flags, and
    /// the quality report counts every rule that fired, not just the decisive one.
    /// </summary>
    public RowJudgement Judge(AisSourceLine line, ParseResult parse)
    {
        var hits = new List<RuleHit>();

        foreach (var rule in LineRules)
        {
            if (rule.Evaluate(line, parse) is { } hit)
            {
                hits.Add(hit);
            }
        }

        if (parse.Record is { } record)
        {
            foreach (var rule in RecordRules)
            {
                if (rule.Evaluate(record) is { } hit)
                {
                    hits.Add(hit);
                }
            }
        }

        return new RowJudgement(hits);
    }
}

/// <summary>Every rule that fired on one row.</summary>
public sealed record RowJudgement(IReadOnlyList<RuleHit> Hits)
{
    public bool Rejected => Hits.Any(h => h.Action == RuleAction.Reject);

    public RuleHit? FirstRejection => Hits.FirstOrDefault(h => h.Action == RuleAction.Reject);

    public IEnumerable<RuleHit> Flags => Hits.Where(h => h.Action == RuleAction.Flag);

    /// <summary>Flag ids as stored in position_report.quality_flags.</summary>
    public string FlagString => string.Join(',', Flags.Select(f => f.RuleId));
}
