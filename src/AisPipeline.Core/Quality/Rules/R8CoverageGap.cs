using AisPipeline.Core.Domain;

namespace AisPipeline.Core.Quality.Rules;

/// <summary>
/// R8 -- the vessel went silent for longer than its reporting interval can explain.
///
/// AIS is received by shore stations with finite range, and a vessel leaving that footprint
/// simply stops appearing; nothing marks its departure. The last fix before a gap frequently
/// shows low speed, because vessels slow near the edges of coverage -- so a state machine that
/// ignores gaps reads the silence as a stop that lasted as long as the absence (ADR-0011).
///
/// Flagging is only half the point. Detection uses the same threshold to close the current stop
/// and mark it incomplete, which is what stops a two-day absence becoming a two-day stop.
/// </summary>
public sealed class R8CoverageGap : ISequenceRule
{
    public R8CoverageGap(TimeSpan maximumGap) => MaximumGap = maximumGap;

    public R8CoverageGap()
        : this(Detection.DetectionThresholds.Default.MaximumGap)
    {
    }

    public TimeSpan MaximumGap { get; }

    public string Id => RuleIds.CoverageGap;

    public string Description => $"No fix for more than {MaximumGap.TotalMinutes:F0} minutes";

    public RuleHit? Evaluate(PositionFix previous, PositionFix current)
    {
        var gap = current.TimestampUtc - previous.TimestampUtc;

        return gap > MaximumGap
            ? new RuleHit(Id, RuleAction.Flag, $"{gap.TotalMinutes:F0} min since the previous fix")
            : null;
    }
}
