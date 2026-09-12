using AisPipeline.Core.Domain;

namespace AisPipeline.Core.Quality.Rules;

/// <summary>
/// R6 -- reported speed is too high to be a tanker under way.
///
/// Written as a defensive guard on the strength of a 1.7M-row sample, in which zero tanker rows
/// exceeded 40 kn while the maximum across the whole feed was 70.8.
///
/// **It is not dormant.** Over the seven-day window it rejects 65 rows and flags 22 -- the sample
/// was simply too small to contain them, and the claim that it had never fired was repeated for
/// months after it stopped being true. Nothing could have caught that until `ais quality` existed
/// to print the counter, which it did not until ADR-0032. Kept for the original reason as well: a
/// corrupt speed surviving into stop detection produces a plausible wrong answer, not an error.
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
