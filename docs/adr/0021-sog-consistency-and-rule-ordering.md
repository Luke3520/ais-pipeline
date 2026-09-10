# 21. SOG consistency rule and annotate-before-detect ordering

Date: 2026-09-11

## Status

Accepted

## Context

Two defects surfaced while calibrating the drift threshold (ADR-0020), both invisible until the
detection pipeline was run end to end on real data.

**A single bad fix destroys a whole stop.** Before teleport-flagged fixes were removed, one stop
reported **2,387 nm** of drift. `max_drift_nm` is the maximum distance from the centroid, and the
centroid is a mean — so one corrupt position both drags the centroid and supplies the maximum,
corrupting a statistic computed over hundreds of otherwise good fixes.

**The teleport rule is too loose for stopped vessels.** After removing fixes implying more than
50 kn, one stop still showed **11.09 nm** of drift while the vessel reported `Moored` throughout. It
moved far enough to be obviously wrong but never fast enough to trip a 50 kn threshold — a gap a
plain teleport rule cannot close, because the rule knows nothing about what the vessel claimed its
speed was.

## Decision

**Rule R11 — SOG consistency.** Cross-check reported speed against the speed implied by consecutive
positions. A vessel reporting under 0.5 kn cannot cover more than roughly 0.5 nm in an hour; where
the implied speed contradicts the reported speed beyond tolerance, flag both fixes.

**Ordering.** The annotate pass (R7 teleport, R8 coverage gap, R11) runs to completion *before*
detection, and **detection excludes flagged fixes from centroid and drift computation**. This was
implicit in the pipeline order and is now explicit, because the failure it prevents is silent: the
stop still appears, with a plausible duration and a nonsensical drift.

Removing 294 teleport fixes from the sample changed the stop count from 52 to 50 and cleaned the
drift distribution.

## Consequences

- Aggregate statistics over a stop are computed only from fixes no rule has flagged as positionally
  unreliable, so one bad row cannot poison a whole event.
- R11 catches a class of error that speed-only and position-only rules both miss, precisely because
  it compares two sources that should agree — the same principle as R10.
- Excluded fixes are still stored and still counted in `fix_count`; they are omitted only from
  geometry. The stop remains fully traceable to its underlying rows.
- The annotate pass and detection cannot be interleaved or parallelised without re-examining this
  ordering.
