namespace AisPipeline.Core.Domain;

/// <summary>
/// The rule ids, defined once.
///
/// These are not an implementation detail of the rules: they are persisted — in
/// <c>quarantine.rule_id</c>, in <c>position_report.quality_flags</c>, or for R10 in
/// <c>stop_event.status_agrees</c> — and they are quoted in
/// the README and in ADRs. That makes them domain vocabulary, which is why they live here rather
/// than only on the rule classes that produce them.
///
/// Ids are never reused, including for retired rules -- R2's id stays R2 even though duplicate
/// handling moved into a database constraint (docs/rules/quality-rules.md).
/// </summary>
public static class RuleIds
{
    public const string UnparseableRow = "R1";
    public const string ExactDuplicate = "R2";
    public const string SameSecondConflict = "R3";
    public const string PositionSentinel = "R4";
    public const string SpeedUnavailable = "R5";
    public const string ImplausibleSpeed = "R6";
    public const string Teleport = "R7";
    public const string CoverageGap = "R8";
    public const string MissingIdentity = "R9";
    /// <summary>
    /// Recorded as <c>stop_event.status_agrees</c>, not as a quarantine row or a quality flag.
    ///
    /// The odd one out: R10 implements rule 4 (record disagreement, do not resolve it) rather than
    /// rule 2, and it judges a stop rather than a row. So it is absent from both tables the quality
    /// report is built from, and `ais quality` reports it in its own right (ADR-0036).
    /// </summary>
    public const string StatusDisagreement = "R10";
    public const string SpeedConsistency = "R11";

    /// <summary>
    /// The mirror of R10: the vessel claims to be stationary while its own speed says otherwise.
    ///
    /// R10 catches a status left on "under way" after the ship stops -- forgotten on arrival. This
    /// catches one left on "moored" or "at anchor" after it leaves -- forgotten on departure. The
    /// two are the same human failure pointed in opposite directions, and until R12 the pipeline
    /// only saw one of them (ADR-0038).
    /// </summary>
    public const string StatusClaimsStationary = "R12";

    /// <summary>
    /// An ETA so far ahead that it is a stale one the decoder rolled forward, not a plan.
    ///
    /// The AIS ETA field carries month, day, hour and minute — and no year. A decoder reading a
    /// date that has already passed must assume the next occurrence, so an ETA nobody updated
    /// reappears up to a year in the future (ADR-0041).
    /// </summary>
    public const string EtaImplausiblyFarAhead = "R13";

    /// <summary>
    /// Rules whose firing means a fix's POSITION cannot be trusted, so it must not contribute to
    /// a centroid or a drift maximum (ADR-0021).
    ///
    /// Declared alongside the ids rather than restated at the point of use: a consumer holding
    /// its own copy of this list gets no compiler signal when an id changes, and the failure is
    /// silent -- the geometry simply stops excluding what it should.
    /// </summary>
    public static readonly IReadOnlySet<string> PositionUnreliable =
        new HashSet<string>(StringComparer.Ordinal) { Teleport, SpeedConsistency };
}
