# 11. Coverage gaps break stops

Date: 2026-09-11

## Status

Accepted

## Context

AIS is received by shore stations with finite range. A vessel that leaves the Danish coverage
footprint simply stops appearing in the feed; nothing marks its departure. The last fix before the
gap frequently shows low speed, because vessels slow near the edges of coverage.

A naive state machine reads that as: entered STOPPED, no fix contradicted it for two days, therefore
a 48-hour stop. That is not a truncated answer, it is a fabricated one — and it gets worse as the
ingest window grows, because longer windows contain more gaps. A multi-day pipeline without gap
handling is strictly worse than a single-day one.

The daily-file boundary creates the same problem at the edges of the dataset. Measured on the sample,
**40 of 130 tankers were stationary across the entire observation window** — already stopped when it
opened and still stopped when it closed — so censored stops are the common case, not an edge case.

## Decision

A gap of more than **60 minutes** between consecutive fixes for a vessel closes the current stop.
Rule R8 flags the fixes on either side of the gap.

`stop_event` carries `is_complete`. It is 0 when the stop touches a coverage gap or either end of the
dataset's time range. **`duration_hours` is only meaningful when `is_complete = 1`**, and query verbs
offer `--complete-only` for that reason.

## Consequences

- The pipeline reports "this vessel was stationary for at least N hours, boundary unknown" instead of
  asserting a duration it cannot support.
- Port calls inherit the flag: a port call containing an incomplete stop is itself incomplete.
- 60 minutes is a heuristic. Class A vessels report every 3 minutes when stationary and every 2–10
  seconds under way, so an hour of silence is well outside normal cadence — but it is a constant,
  exposed for tuning and stated as such.
- This is the single change that makes a multi-day window safe, which is what makes complete port
  calls reachable at all.
