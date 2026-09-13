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
    /// 60, and it sits inside a run of days on which **no fix at all** carries an ETA. Measured
    /// across all 4.8M fixes carrying one over the seven-day window: 4,012,881 fall within 14 days
    /// and 438,762 more within 30; the count then falls away through 30, 31, 32 and 33 days, shows
    /// 127 fixes at exactly 41 and 5 at exactly 77 -- and is <b>empty for every day from 42 to
    /// 76</b>. Beyond that it climbs again to 39,713 in the 90-180 band and 313,351 past 180,
    /// clustering at about a year.
    ///
    /// So every threshold between 42 and 76 classifies exactly the same rows, and the precise
    /// value provably cannot matter. That is a stronger footing than any other constant here:
    /// ADR-0020's berth distance was a guess that later moved by a factor of thirty, and R12's
    /// sits in a trough that is shallow rather than empty.
    ///
    /// ADR-0041 originally put the empty band at 30-90 days, measured on a three-million-line
    /// slice of a single day, and told the reader to re-check the gap against a full rebuild. The
    /// full seven days narrowed it to 42-76. The threshold was already inside both, so nothing it
    /// classifies changed -- but the range as that record states it is wrong, and the index carries
    /// the correction.
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
