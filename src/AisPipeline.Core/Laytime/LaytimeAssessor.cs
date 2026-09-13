using AisPipeline.Core.Domain;

namespace AisPipeline.Core.Laytime;

/// <summary>Why a port call could not be priced.</summary>
public enum LaytimeRefusal
{
    /// <summary>It was priced.</summary>
    None,

    /// <summary>No berth phase, so there were no cargo operations to measure.</summary>
    NoBerthPhase,

    /// <summary>
    /// Hours inside the berth span have geometry the pipeline does not stand behind.
    ///
    /// Neither excluding them (which favours the charterer) nor counting them (which favours the
    /// owner) is supportable, so no figure is produced at all (ADR-0025).
    /// </summary>
    BerthGeometryUntrustworthy,
}

/// <summary>
/// A priced statement, or a named reason there isn't one.
///
/// The refusal is a value rather than an exception or a console message, because both surfaces
/// have to render it and a demurrage figure that silently failed to appear is worse than one that
/// is wrong -- at least a wrong figure gets argued with (ADR-0042).
/// </summary>
public sealed record LaytimeAssessment
{
    public LaytimeStatement? Statement { get; init; }

    public LaytimeRefusal Refusal { get; init; }

    /// <summary>One sentence naming what could not be measured, for a human reading the refusal.</summary>
    public string? RefusalDetail { get; init; }

    public VoyageTimeline? Timeline { get; init; }

    public bool IsPriced => Statement is not null;
}

/// <summary>
/// Turns a port call and a set of charter party terms into a laytime statement, or refuses.
///
/// Pure, and in Core, so the CLI and the API cannot disagree about when a call is priceable. The
/// two previously carried their own copies of this sequence, and the copies had already drifted
/// once (ADR-0042).
/// </summary>
public static class LaytimeAssessor
{
    public static LaytimeAssessment Assess(PortCall portCall, CharterPartyTerms terms)
    {
        if (VoyageTimeline.FromPortCall(portCall) is not { } timeline)
        {
            return new LaytimeAssessment
            {
                Refusal = LaytimeRefusal.NoBerthPhase,
                RefusalDetail =
                    "this port call has no berth phase, so there are no cargo operations to measure",
            };
        }

        if (!timeline.BerthSpanIsTrustworthy)
        {
            return new LaytimeAssessment
            {
                Refusal = LaytimeRefusal.BerthGeometryUntrustworthy,
                Timeline = timeline,
                RefusalDetail =
                    $"{timeline.UntrustworthyHoursInBerthSpan:F2}h inside the berth span have " +
                    "geometry the pipeline does not stand behind, so this call cannot be priced",
            };
        }

        return new LaytimeAssessment
        {
            Timeline = timeline,
            Statement = new LaytimeCalculator().Calculate(
                terms,
                timeline.BerthedUtc!.Value,
                timeline.DepartedBerthUtc!.Value),
        };
    }
}
