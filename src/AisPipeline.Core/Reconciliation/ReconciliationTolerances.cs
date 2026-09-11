namespace AisPipeline.Core.Reconciliation;

/// <summary>
/// How far a Statement of Facts time may sit from the AIS observation before it counts as a
/// disagreement.
///
/// These are **offsets, not just tolerances**, and that distinction is the whole point. A vessel
/// stops moving well before it is made fast, so comparing "all fast" to the AIS stop and expecting
/// zero would flag every honest document in the world. The expected gap is subtracted first; only
/// what remains is a discrepancy.
/// </summary>
public sealed record ReconciliationTolerances
{
    /// <summary>
    /// How long after AIS sees the vessel stop that a Statement of Facts records "all fast".
    ///
    /// **Inferred from two real documents, not measured against AIS.** Both bracket the berthing
    /// sequence themselves:
    ///
    /// <list type="bullet">
    /// <item>Immingham, Aug 2023: first line ashore 11:42, all fast 12:12 — 30 minutes.
    /// Tug made fast 11:12 to all fast — 60 minutes.</item>
    /// <item>Chimbote, May 2018: first line 05:15, all fast 06:16 — 61 minutes.</item>
    /// </list>
    ///
    /// AIS sees the vessel stop around "first line", so the expected lag is roughly 30–60 minutes.
    /// The midpoint is the default and the band covers both observations with room either side.
    ///
    /// This matters commercially rather than cosmetically: many charter parties start laytime only
    /// once the vessel is all fast, so at 28,000 USD/day an hour of error here is several hundred
    /// dollars on a single line item (ADR-0031).
    /// </summary>
    public TimeSpan AllFastLagAfterAisStop { get; init; } = TimeSpan.FromMinutes(45);

    /// <summary>Either side of that expected lag. Covers the 30–61 minutes actually observed.</summary>
    public TimeSpan AllFastTolerance { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Tolerance for events where the physical moment and the recorded moment should coincide:
    /// dropping anchor, weighing it, last line off.
    ///
    /// **Uncalibrated.** No document available measures these against AIS, so this is a stated
    /// default rather than a finding — the same status the berth threshold held in ADR-0020 before
    /// seven days of data moved it by a factor of thirty. Treat a discrepancy near this boundary
    /// as a question, not an answer.
    /// </summary>
    public TimeSpan PhysicalEventTolerance { get; init; } = TimeSpan.FromMinutes(15);

    public static ReconciliationTolerances Default { get; } = new();
}
