using AisPipeline.Core.Domain;

namespace AisPipeline.Core.Quality.Rules;

/// <summary>
/// R13 -- the vessel's ETA is too far ahead to be a plan.
///
/// The third hand-entered field this pipeline checks, after the navigational status (R10, R12).
/// It goes stale the same way, but it shows up inverted, and the reason is in the AIS encoding:
/// <b>the ETA field has no year</b>. ITU-R M.1371 gives it month, day, hour and minute only, so a
/// decoder meeting a date that has already passed has to assume the next occurrence of it. An ETA
/// nobody has touched therefore does not drift into the past -- it is rolled forward and reappears
/// up to a year in the future.
///
/// Which is why this rule does NOT flag an ETA in the past. That is a ship running late, and every
/// forecast is sometimes wrong; the furthest past observed was six hours. Lateness is not a data
/// defect and flagging it would bury this one (ADR-0041).
/// </summary>
public sealed class R13EtaImplausiblyFarAhead : IRecordRule
{
    /// <summary>
    /// How far ahead an ETA stops being a plan, in days.
    ///
    /// 60, and the number is unusually well behaved: the observed distribution has a **literal
    /// gap** on either side of it. Across 120,225 fixes carrying an ETA, 95,075 fall within 14
    /// days and 12,526 within 30 -- then <b>nothing at all</b> between 30 and 90 days -- and then
    /// 911, 1,679, 628 and 8,213 in the bands beyond, clustering hard at about one year.
    ///
    /// So any threshold between 30 and 90 days classifies exactly the same rows. Unlike ADR-0020's
    /// berth distance, which was a guess that later moved by a factor of thirty, and unlike R12's,
    /// which sits in a trough that is shallow rather than empty, the precise value here provably
    /// changes nothing. 60 is the middle of the empty band.
    /// </summary>
    public const int ImplausibleAfterDays = 60;

    public string Id => RuleIds.EtaImplausiblyFarAhead;

    public string Description => "ETA more than 60 days ahead; a stale one the decoder rolled over";

    public RuleHit? Evaluate(RawAisRecord record)
    {
        if (record.EtaUtc is not { } eta)
        {
            return null;
        }

        var ahead = eta - record.TimestampUtc;

        return ahead.TotalDays > ImplausibleAfterDays
            ? new RuleHit(
                Id,
                RuleAction.Flag,
                $"ETA {eta:yyyy-MM-dd HH:mm} is {ahead.TotalDays:F0} days ahead of this fix")
            : null;
    }
}
