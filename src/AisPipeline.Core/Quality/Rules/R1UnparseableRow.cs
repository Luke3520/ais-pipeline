using AisPipeline.Core.Domain;
using AisPipeline.Core.Parsing;

namespace AisPipeline.Core.Quality.Rules;

/// <summary>
/// R1 -- the line could not be read as an AIS row: wrong field count, or a mandatory value
/// that would not parse.
///
/// Measured: ~0 rows with a conformant CSV reader, against 4,261 in a 1.7M-row sample when the
/// line is split naively on commas (ADR-0008). The rule is the guard that keeps that true.
/// </summary>
public sealed class R1UnparseableRow : ILineRule
{
    public string Id => RuleIds.UnparseableRow;

    public string Description => "Row could not be parsed: wrong field count or malformed value";

    public RuleHit? Evaluate(AisSourceLine line, ParseResult parse) =>
        parse.Ok ? null : new RuleHit(Id, RuleAction.Reject, parse.Failure!);
}
