using AisPipeline.Core.Domain;
using AisPipeline.Core.Geo;

namespace AisPipeline.Core.Quality.Rules;

/// <summary>
/// R7 -- the vessel appears to have moved impossibly fast between consecutive fixes.
///
/// Requires BOTH an impossible speed and a real distance. A speed-only threshold at 50 kn fires
/// on 2,084 pairs a day, of which 87% moved less than 100 metres: that is GPS scatter at a
/// 2-second reporting interval, not a teleport. Adding the distance gate leaves 48, and those
/// are genuine -- the extreme is a 3,040 nm jump in 10 seconds (ADR-0023).
///
/// The gate is load-bearing rather than cosmetic. Detection excludes R7-flagged fixes from
/// centroid and drift, so a speed-only rule would strip ~1,800 good fixes a day out of exactly
/// the calculation that separates a berth from an anchorage.
/// </summary>
public sealed class R7Teleport : ISequenceRule
{
    /// <summary>Above this implied speed the movement is not navigation.</summary>
    public const double ImpossibleSpeedKn = 50.0;

    /// <summary>
    /// Below this distance the pair is ignored however high the implied speed, because no
    /// stationary vessel's GPS scatter reaches half a nautical mile.
    /// </summary>
    public const double MinimumDistanceNm = 0.5;

    public string Id => "R7";

    public string Description =>
        $"Implied speed over {ImpossibleSpeedKn} kn across more than {MinimumDistanceNm} nm";

    public RuleHit? Evaluate(PositionFix previous, PositionFix current)
    {
        var distanceNm = Haversine.DistanceNm(
            previous.Latitude, previous.Longitude, current.Latitude, current.Longitude);

        if (distanceNm <= MinimumDistanceNm)
        {
            return null;
        }

        // Null when no time elapsed: 0.40% of consecutive pairs share a vessel-second, so this
        // divides by zero on real data rather than in theory (ADR-0021).
        if (Haversine.ImpliedSpeedKn(distanceNm, current.TimestampUtc - previous.TimestampUtc)
            is not { } impliedKn || impliedKn <= ImpossibleSpeedKn)
        {
            return null;
        }

        return new RuleHit(Id, RuleAction.Flag,
            $"{distanceNm:F1} nm in {(current.TimestampUtc - previous.TimestampUtc).TotalSeconds:F0}s " +
            $"implies {impliedKn:F0} kn");
    }
}
