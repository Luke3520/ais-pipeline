using AisPipeline.Core.Domain;
using AisPipeline.Core.Ports;
using AisPipeline.Core.Quality;

namespace AisPipeline.Core.Annotate;

/// <summary>A fix whose flags are still being decided, held until its pair has been judged.</summary>
internal sealed record Pending(AisPipeline.Core.Domain.PositionFix Fix, List<string> Flags);

/// <summary>A fix whose flags changed, and what they became.</summary>
public sealed record FlagUpdate(long PositionId, string QualityFlags);

/// <summary>Outcome of one annotate pass.</summary>
public sealed record AnnotateResult(long FixesExamined, IReadOnlyDictionary<string, long> RuleHits);

/// <summary>
/// Applies the sequence rules -- the ones that need a vessel's fixes in time order.
///
/// Reads from the store ordered by (mmsi, ts_utc), never from a file stream. That ordering is
/// what makes these rules correct across day boundaries instead of resetting at every file
/// (docs/rules/quality-rules.md), and it is why they cannot run during ingest.
///
/// Must complete before detection: detection excludes flagged fixes from centroid and drift,
/// so running them the other way round would let a corrupt position into the geometry
/// (ADR-0021).
/// </summary>
public sealed class AnnotatePass
{
    private readonly IAisStore _store;
    private readonly IReadOnlyList<ISequenceRule> _rules;
    private readonly int _batchSize;

    public AnnotatePass(IAisStore store, IReadOnlyList<ISequenceRule> rules, int batchSize = 5_000)
    {
        _store = store;
        _rules = rules;
        _batchSize = batchSize;
    }

    /// <summary>Ids this pass owns, and therefore may recompute from scratch.</summary>
    private HashSet<string> OwnedIds => [.. _rules.Select(r => r.Id)];

    public AnnotateResult Run()
    {
        var owned = OwnedIds;
        var hits = new Dictionary<string, long>(StringComparer.Ordinal);
        var updates = new List<FlagUpdate>(_batchSize);
        long examined = 0;

        // A sequence rule judges a PAIR, and both halves of that pair are implicated -- ADR-0011
        // requires R8 to flag "the fixes on either side of the gap", ADR-0021 requires R11 to
        // "flag both fixes". So the earlier fix's flags cannot be finalised until the next fix
        // has been read and the rules have run against the pair. One fix is held back for that.
        //
        // Flagging only the later fix would leave the earlier one -- often the one actually
        // carrying the bad position -- unflagged and therefore still contributing to the
        // centroid and still able to supply the drift maximum, which is exactly the aggregate
        // poisoning the flag-and-exclude mechanism exists to prevent.
        Pending? pending = null;

        void Flush(Pending done)
        {
            var recomputed = string.Join(',', done.Flags);
            if (!string.Equals(recomputed, done.Fix.QualityFlags, StringComparison.Ordinal))
            {
                updates.Add(new FlagUpdate(done.Fix.Id, recomputed));

                if (updates.Count >= _batchSize)
                {
                    _store.UpdateQualityFlags(updates);
                    updates.Clear();
                }
            }
        }

        foreach (var fix in _store.ReadFixesOrdered())
        {
            examined++;

            // Strip the ids this pass owns before recomputing them. Without this, running
            // annotate twice would append R7 to a fix that already carried it, and the flag
            // string would grow on every run -- a change where re-running is not a no-op.
            // Ingest-time flags (R5, R6) are not owned here and are preserved untouched.
            var kept = fix.QualityFlags
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Where(id => !owned.Contains(id))
                .ToList();

            var current = new Pending(fix, kept);

            if (pending is { } earlier && earlier.Fix.Mmsi == fix.Mmsi)
            {
                foreach (var rule in _rules)
                {
                    if (rule.Evaluate(earlier.Fix, fix) is { } hit)
                    {
                        // Both halves. The count stays per-pair: one firing, two fixes marked.
                        if (!earlier.Flags.Contains(hit.RuleId, StringComparer.Ordinal))
                        {
                            earlier.Flags.Add(hit.RuleId);
                        }

                        if (!current.Flags.Contains(hit.RuleId, StringComparer.Ordinal))
                        {
                            current.Flags.Add(hit.RuleId);
                        }

                        hits[hit.RuleId] = hits.GetValueOrDefault(hit.RuleId) + 1;
                    }
                }
            }

            if (pending is { } complete)
            {
                Flush(complete);
            }

            pending = current;
        }

        if (pending is { } last)
        {
            Flush(last);
        }

        if (updates.Count > 0)
        {
            _store.UpdateQualityFlags(updates);
        }

        return new AnnotateResult(examined, hits);
    }
}
