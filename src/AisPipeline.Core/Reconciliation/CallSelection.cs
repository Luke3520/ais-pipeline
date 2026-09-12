namespace AisPipeline.Core.Reconciliation;

/// <summary>A port call as the selector sees it: an id and the span it covers.</summary>
public sealed record CallCandidate(long Id, DateTime ArrivedUtc, DateTime DepartedUtc);

/// <summary>
/// Which port call a document describes, chosen by how much time it shares with each.
///
/// ADR-0033 gated the comparison: a document whose window does not meet the call's is refused
/// rather than priced. It deliberately left the selection alone, so `reconcile` still asked for the
/// vessel's most recent complete call and then validated that guess. The effect was that correct
/// historical documents became refusals -- the gate turned wrong answers into refusals without
/// making right answers reachable (ADR-0035).
///
/// Pure, and over ids and spans rather than stored rows, so the choice can be tested without a
/// database and the adapter decides nothing.
/// </summary>
public sealed record CallSelection
{
    public required DateTime DocumentFromUtc { get; init; }

    public required DateTime DocumentToUtc { get; init; }

    /// <summary>Every candidate that shares time with the document, most shared first.</summary>
    public required IReadOnlyList<(CallCandidate Call, TimeSpan Overlap)> Ranked { get; init; }

    /// <summary>
    /// The call to compare against, or null when there is no single best answer.
    ///
    /// Null for no overlap at all, and null for a tie. A tie is not broken by id or by recency:
    /// those would be arbitrary, and an arbitrary choice here silently decides which timeline a
    /// demurrage figure is measured against.
    /// </summary>
    public CallCandidate? Chosen =>
        Ranked.Count == 1 || (Ranked.Count > 1 && Ranked[0].Overlap > Ranked[1].Overlap)
            ? Ranked[0].Call
            : null;

    public bool NothingOverlaps => Ranked.Count == 0;

    /// <summary>Two or more candidates share exactly as much time with the document.</summary>
    public bool IsAmbiguous => Ranked.Count > 1 && Ranked[0].Overlap == Ranked[1].Overlap;

    /// <summary>
    /// Candidates that overlap but were not chosen.
    ///
    /// Reported rather than dropped. A document overlapping two calls is usually one genuine match
    /// and one neighbouring visit clipped at the edge, but it can also be a vessel that called
    /// twice in quick succession -- and the reader is the one who can tell.
    /// </summary>
    public IReadOnlyList<(CallCandidate Call, TimeSpan Overlap)> AlsoOverlapping =>
        [.. Ranked.Skip(Chosen is null ? 0 : 1)];

    public static CallSelection Select(
        DateTime documentFromUtc,
        DateTime documentToUtc,
        IReadOnlyList<CallCandidate> candidates)
    {
        var ranked = candidates
            .Select(c => (Call: c, Overlap: Overlap(documentFromUtc, documentToUtc, c)))
            // Strictly positive, the same bar CallMatch applies: touching windows share no time,
            // and zero shared time is not evidence that two records describe one visit.
            .Where(x => x.Overlap > TimeSpan.Zero)
            .OrderByDescending(x => x.Overlap)
            .ThenBy(x => x.Call.ArrivedUtc)
            .ToList();

        return new CallSelection
        {
            DocumentFromUtc = documentFromUtc,
            DocumentToUtc = documentToUtc,
            Ranked = ranked,
        };
    }

    private static TimeSpan Overlap(DateTime fromUtc, DateTime toUtc, CallCandidate call)
    {
        var start = fromUtc > call.ArrivedUtc ? fromUtc : call.ArrivedUtc;
        var end = toUtc < call.DepartedUtc ? toUtc : call.DepartedUtc;
        return end - start;
    }
}
