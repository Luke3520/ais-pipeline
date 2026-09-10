# 10. Stop detection thresholds and hysteresis

Date: 2026-09-11

## Status

Accepted

## Context

A stop is the raw material of everything downstream: port calls, waiting versus working time, and
eventually laytime. It has to be derived from speed over ground, which is noisy near zero — a moored
vessel's reported SOG fluctuates around 0.0–0.3 kn, and a single threshold would emit dozens of
spurious stop events as it flickers across the line.

## Decision

A state machine over one vessel's fixes in time order, read from the store ordered by
`(mmsi, ts_utc)` — never from the file stream, so it is correct across day boundaries.

- **Enter** STOPPED when SOG < 0.5 kn.
- **Leave** STOPPED when SOG ≥ 1.0 kn.

The gap between the two thresholds is hysteresis: a vessel bobbing around 0.5 kn produces one stop
event, not fifty.

- Emit a `stop_event` only when the stopped period lasted **≥ 30 minutes**.
- A fix with **unavailable SOG is *unknown*, not stopped** — it neither enters nor exits the state.
  Treating a missing value as zero would fabricate stops (rule R5 flags these).
- `centroid` is the mean position of the stopped fixes; `max_drift_nm` is the furthest any fix sat
  from that centroid.
- `reported_status` is the modal navigational status during the stop; `status_agrees` records
  whether it contradicts the speed-derived conclusion (rule R10).

All 16 AIS navigational statuses must be mapped. The tanker data includes `Constrained by her
draught` on 18,203 sampled rows, which an under-way/at-rest binary would not anticipate.

## Consequences

- Thresholds are constants, exposed for tuning, and stated in the README as heuristics rather than
  facts.
- The state machine is a pure function over a fix sequence and is unit-tested against a hand-built
  case: moving → stopped 45 min → moving → stopped 12 h → coverage gap → stopped.
- `max_drift_nm` records evidence rather than classifying: small means moored alongside, large means
  swinging at anchor. The threshold that separates them is a separate decision (ADR-0020).
- Measured on the sample, 7.5% of tanker fixes sit below 0.5 kn, so stops are common enough to
  validate against real vessels rather than synthetic data alone.
