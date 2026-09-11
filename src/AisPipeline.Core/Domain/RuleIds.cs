namespace AisPipeline.Core.Domain;

/// <summary>
/// The rule ids, defined once.
///
/// These are not an implementation detail of the rules: they are persisted, in
/// <c>quarantine.rule_id</c> and in <c>position_report.quality_flags</c>, and they are quoted in
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
    public const string StatusDisagreement = "R10";
    public const string SpeedConsistency = "R11";

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
