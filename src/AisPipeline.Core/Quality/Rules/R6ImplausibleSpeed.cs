using AisPipeline.Core.Domain;

namespace AisPipeline.Core.Quality.Rules;

/// <summary>
/// R6 -- reported speed is too high to be a tanker under way.
///
/// A defensive guard rather than a discovered defect: zero tanker rows in a 1.7M-row sample
/// exceed 40 kn, and the maximum observed across the whole feed was 70.8. Kept because a
/// corrupt speed that survives into stop detection produces a plausible wrong answer rather
/// than an error, and stated in the README as a guard that has never fired.
/// </summary>
public sealed class R6ImplausibleSpeed : IRecordRule
{
    /// <summary>Above this, the reading is rejected outright.</summary>
    public const double RejectAboveKn = 40.0;

    /// <summary>At or above this but not above <see cref="RejectAboveKn"/>, the row is flagged.</summary>
    public const double FlagAtOrAboveKn = 30.0;

    public string Id => RuleIds.ImplausibleSpeed;

    public string Description => "Speed over ground implausible for a tanker";

    public RuleHit? Evaluate(RawAisRecord record)
    {
        if (record.SpeedOverGroundKn is not { } sog)
        {
            // Unavailable speed is R5's business, not this rule's.
            return null;
        }

        if (sog > RejectAboveKn)
        {
            return new RuleHit(Id, RuleAction.Reject, $"SOG {sog} kn exceeds {RejectAboveKn} kn");
        }

        return sog >= FlagAtOrAboveKn
            ? new RuleHit(Id, RuleAction.Flag, $"SOG {sog} kn is unusually high")
            : null;
    }
}
