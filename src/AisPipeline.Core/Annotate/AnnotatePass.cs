using AisPipeline.Core.Domain;
using AisPipeline.Core.Ports;
using AisPipeline.Core.Quality;

namespace AisPipeline.Core.Annotate;

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

        PositionFix? previous = null;

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

            if (previous is not null && previous.Mmsi == fix.Mmsi)
            {
                foreach (var rule in _rules)
                {
                    if (rule.Evaluate(previous, fix) is { } hit)
                    {
                        kept.Add(hit.RuleId);
                        hits[hit.RuleId] = hits.GetValueOrDefault(hit.RuleId) + 1;
                    }
                }
            }

            var recomputed = string.Join(',', kept);
            if (!string.Equals(recomputed, fix.QualityFlags, StringComparison.Ordinal))
            {
                updates.Add(new FlagUpdate(fix.Id, recomputed));

                if (updates.Count >= _batchSize)
                {
                    _store.UpdateQualityFlags(updates);
                    updates.Clear();
                }
            }

            previous = fix;
        }

        if (updates.Count > 0)
        {
            _store.UpdateQualityFlags(updates);
        }

        return new AnnotateResult(examined, hits);
    }
}
