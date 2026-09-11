using AisPipeline.Core.Domain;
using AisPipeline.Core.Geo;

namespace AisPipeline.Core.Quality.Rules;

/// <summary>
/// R11 -- the vessel's reported speed contradicts the speed its own positions imply.
///
/// Exists because R7 cannot catch this class. A vessel reporting 0.0 kn that moves 11 nm has
/// gone somewhere it says it did not go, but it never trips a 50 kn threshold -- one stop in a
/// sampled day showed exactly that, drifting 11.09 nm while reporting "Moored" throughout
/// (ADR-0021).
///
/// The same principle as R10: two sources that should agree about one fact, compared rather
/// than trusted. A speed-only or position-only rule is structurally unable to see it.
/// </summary>
public sealed class R11SpeedConsistency : ISequenceRule
{
    /// <summary>
    /// The implied speed must exceed the reported speed by this factor AND by
    /// <see cref="MinimumExcessKn"/> before the disagreement counts. A ratio alone would fire
    /// constantly near zero, where a reported 0.1 kn and an implied 0.3 kn differ threefold and
    /// mean nothing.
    /// </summary>
    public const double ExcessFactor = 3.0;

    /// <summary>Absolute margin, so ordinary GPS scatter at low speed is not a finding.</summary>
    public const double MinimumExcessKn = 3.0;

    public string Id => "R11";

    public string Description => "Reported speed contradicts the speed implied by consecutive positions";

    public RuleHit? Evaluate(PositionFix previous, PositionFix current)
    {
        // The claim under test is the earlier fix's: it asserted a speed, and the distance to
        // the next fix is the evidence about what the vessel then did.
        if (previous.SpeedOverGroundKn is not { } reportedKn)
        {
            return null;
        }

        var distanceNm = Haversine.DistanceNm(
            previous.Latitude, previous.Longitude, current.Latitude, current.Longitude);

        if (Haversine.ImpliedSpeedKn(distanceNm, current.TimestampUtc - previous.TimestampUtc)
            is not { } impliedKn)
        {
            return null;
        }

        var excessive = impliedKn > (reportedKn * ExcessFactor)
            && impliedKn > (reportedKn + MinimumExcessKn);

        return excessive
            ? new RuleHit(Id, RuleAction.Flag,
                $"reported {reportedKn:F1} kn but positions imply {impliedKn:F1} kn")
            : null;
    }
}
