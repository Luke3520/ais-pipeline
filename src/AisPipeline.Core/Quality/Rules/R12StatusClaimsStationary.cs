using AisPipeline.Core.Domain;

namespace AisPipeline.Core.Quality.Rules;

/// <summary>
/// R12 -- the vessel reports a stationary status while its own speed says it is making way.
///
/// The mirror of R10, and the half the pipeline could not see. R10 is a property of a detected
/// stop: the speed said stationary, the status said under way, so a status was left uncorrected on
/// arrival. R12 is a property of a single row: the status says moored or anchored, the speed says
/// several knots, so a status was left uncorrected on departure.
///
/// Flags, never rejects. The position and speed on such a row are measured by the receiver and are
/// perfectly good; it is the hand-typed status that is doubtful, and discarding a sound fix because
/// a human forgot a dial would lose real track for no gain (ADR-0038).
/// </summary>
public sealed class R12StatusClaimsStationary : IRecordRule
{
    /// <summary>
    /// Above this, a stationary claim is contradicted.
    ///
    /// 3.0 kn, taken from the trough of a bimodal distribution rather than asserted. Speed over
    /// ground on the 227,258 fixes claiming "Moored" or "At anchor" across the seven-day window
    /// decays monotonically -- 157,515 at exactly zero, 60,618 below 0.5 kn, 3,908, 748 -- to a
    /// minimum of 326 in the 2-3 kn band, and then RISES again: 1,267, 1,047, and 1,808 above
    /// 8 kn. Two populations, and the minimum between them is the boundary.
    ///
    /// Below it sits a moored vessel working against tide and GPS noise. Above it sits a ship under
    /// way. This is a measured threshold, unlike the berth distance ADR-0020 first guessed at and
    /// later had to move by a factor of thirty -- but it is still a heuristic, and a fix either
    /// side of it is a question rather than an answer.
    /// </summary>
    public const double ContradictedAboveKn = 3.0;

    public string Id => RuleIds.StatusClaimsStationary;

    public string Description => "Reported status claims stationary while speed says under way";

    public RuleHit? Evaluate(RawAisRecord record)
    {
        if (record.SpeedOverGroundKn is not { } sog)
        {
            // No speed to contradict the claim with. R5 owns the unavailable-speed case.
            return null;
        }

        if (NavigationalStatus.Classify(record.NavigationalStatus) != ReportedActivity.Stationary)
        {
            return null;
        }

        return sog > ContradictedAboveKn
            ? new RuleHit(
                Id,
                RuleAction.Flag,
                $"status '{record.NavigationalStatus}' claims stationary at {sog} kn")
            : null;
    }
}
