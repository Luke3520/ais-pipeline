using AisPipeline.Core.Domain;
using AisPipeline.Core.Sof;

namespace AisPipeline.Core.Reconciliation;

/// <summary>
/// Whether a Statement of Facts and a port call describe the same visit.
///
/// The question nothing was asking. A document was matched to AIS by MMSI and "most recent
/// complete call", so a statement for a call in March would reconcile against the vessel's most
/// recent call in September, and the difference between two unrelated timelines would be priced in
/// demurrage and printed without a word of doubt. That is worse than a wrong number: it is a wrong
/// number with an audit trail behind it.
///
/// The test is overlap, deliberately, rather than a tuned proximity threshold. A document and the
/// call it describes necessarily share time -- a Notice of Readiness is tendered at the anchorage,
/// which is inside the call, and the last line comes off at its end. Requiring genuine overlap
/// needs no calibrated constant, which matters here: every threshold this project guessed at has
/// since had to move (ADR-0020 moved by a factor of thirty).
/// </summary>
public sealed record CallMatch
{
    public required DateTime DocumentFromUtc { get; init; }
    public required DateTime DocumentToUtc { get; init; }
    public required DateTime AisFromUtc { get; init; }
    public required DateTime AisToUtc { get; init; }

    /// <summary>Shared time. Zero or negative means the two windows do not meet at all.</summary>
    public TimeSpan Overlap { get; init; }

    /// <summary>
    /// The port the document names. Never compared, because AIS has no port identity: a stop is a
    /// centroid, and nothing in the store says which port a centroid sits in.
    ///
    /// Carried so the gap is visible in the result rather than merely absent from it. Half of the
    /// matching evidence a human would use -- "this document says Immingham" -- is unavailable,
    /// and a caller deciding how much to trust a match should be told that, not left to infer it.
    /// </summary>
    public required string DocumentPort { get; init; }

    /// <summary>False until stops resolve to ports. Then this becomes a real check.</summary>
    public bool PortWasChecked => false;

    public bool TimesOverlap => Overlap > TimeSpan.Zero;

    /// <summary>
    /// The verdict, on the evidence available. Times only, which is why it is named for what it
    /// tested rather than asserting the two are the same call.
    /// </summary>
    public bool TimesAreConsistent => TimesOverlap;

    /// <summary>How far apart the windows are when they do not overlap. Zero when they do.</summary>
    public TimeSpan Gap => TimesOverlap ? TimeSpan.Zero : -Overlap;

    public static CallMatch Evaluate(StatementOfFacts sof, PortCall portCall)
    {
        if (sof.Events.Count == 0)
        {
            throw new ArgumentException(
                "the statement has no events, so there is nothing to match against a port call",
                nameof(sof));
        }

        var documentFrom = sof.Events.Min(e => e.TimestampUtc);
        var documentTo = sof.Events.Max(e => e.TimestampUtc);

        // Overlap of two closed intervals. Negative when they are disjoint, and then its magnitude
        // is the gap between them -- which is the number a refusal message needs.
        var overlapStart = documentFrom > portCall.ArrivedUtc ? documentFrom : portCall.ArrivedUtc;
        var overlapEnd = documentTo < portCall.DepartedUtc ? documentTo : portCall.DepartedUtc;

        return new CallMatch
        {
            DocumentFromUtc = documentFrom,
            DocumentToUtc = documentTo,
            AisFromUtc = portCall.ArrivedUtc,
            AisToUtc = portCall.DepartedUtc,
            Overlap = overlapEnd - overlapStart,
            DocumentPort = sof.Port,
        };
    }

    /// <summary>One line for a refusal message, naming both windows so the reader can see why.</summary>
    public string Explain() =>
        TimesOverlap
            ? $"document {DocumentFromUtc:yyyy-MM-dd HH:mm}-{DocumentToUtc:yyyy-MM-dd HH:mm} overlaps " +
              $"the AIS call {AisFromUtc:yyyy-MM-dd HH:mm}-{AisToUtc:yyyy-MM-dd HH:mm} " +
              $"by {Overlap.TotalHours:F1}h"
            : $"document covers {DocumentFromUtc:yyyy-MM-dd HH:mm} to {DocumentToUtc:yyyy-MM-dd HH:mm}, " +
              $"the AIS call covers {AisFromUtc:yyyy-MM-dd HH:mm} to {AisToUtc:yyyy-MM-dd HH:mm}; " +
              $"they do not overlap, and are {Gap.TotalHours:F1}h apart";
}
