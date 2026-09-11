using AisPipeline.Core.Domain;

namespace AisPipeline.Core.Quality.Rules;

/// <summary>
/// R5 -- speed over ground was not reported.
///
/// Flags rather than rejects, deliberately: the row is still a valid, located observation, and
/// discarding it would punch a hole in the vessel's track for a reason unrelated to where the
/// vessel was. The speed is stored as null, and stop detection treats null as *unknown* --
/// neither entering nor leaving the stopped state. Coalescing it to zero would fabricate stops.
///
/// Measured at 8.5% of feed rows, 0.012% of tanker rows.
/// </summary>
public sealed class R5SpeedUnavailable : IRecordRule
{
    public string Id => "R5";

    public string Description => "Speed over ground unavailable; stored as null and flagged";

    public RuleHit? Evaluate(RawAisRecord record) =>
        record.SpeedOverGroundKn is null
            ? new RuleHit(Id, RuleAction.Flag, "SOG not reported")
            : null;
}
