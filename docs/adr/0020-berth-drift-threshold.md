# 20. Berth/anchorage drift threshold, calibrated

Date: 2026-09-11

## Status

Superseded by [ADR-0024](0024-berth-threshold-scales-with-duration.md).

The method here stands and ADR-0024 reuses it. What changed is the model: a fixed threshold
cannot hold, because a moored vessel's measured drift grows with how long it sits there.

## Context

A port call is only commercially interesting if its phases can be separated: time spent *waiting* at
anchor versus time spent *working* alongside a berth. That distinction is the input a laytime
calculation needs.

The available signal is `max_drift_nm` — how far the vessel wandered from the centroid of its stopped
fixes. A vessel moored alongside is held by lines and barely moves; a vessel at anchor swings on its
chain with tide and wind. The initial plan assumed a threshold of **0.3 nm** separated them.

That figure was a guess and had never been checked. Since the entire port-call output depends on it,
it was validated before any of it was built.

## Decision

**Berth is `max_drift_nm < 0.01` (≈ 19 m).** The assumed 0.3 nm is wrong by a factor of thirty and
performs worse than a coin flip.

Method: the full state machine was run over a 1,717,280-row sample (2h40m of `aisdk-2026-09-05.csv`)
after deduplication, R4 and R7, yielding 50 stops of ≥ 30 minutes. Each vessel's *own* reported
navigational status was used as a semi-independent label, and drift was tested for its ability to
separate `Moored` from `At anchor`:

| Threshold | ≈ metres | Moored correct | Anchored correct | Accuracy |
|---|---|---|---|---|
| 0.005 nm | 9 | 18/21 | 17/17 | **92.1%** |
| **0.010 nm** | **19** | **19/21** | **16/17** | **92.1%** |
| 0.030 nm | 56 | 20/21 | 8/17 | 73.7% |
| 0.100 nm | 185 | 20/21 | 1/17 | 55.3% |
| 0.300 nm | 556 | 20/21 | 0/17 | 52.6% |

The populations do separate, just thirty times more finely than assumed. Moored: median **0.0021 nm
(3.9 m)**, p75 0.0032. Anchored: median **0.0299 nm (55 m)**, min 0.0098. Moored p75 and anchored min
sit 3× apart with almost no overlap.

## Consequences

- The threshold is a named constant, exposed for tuning, and presented in the README as recording
  evidence rather than classifying berths.
- **0.01 nm is a conservative floor.** A 2h40m window captures only a partial arc of an anchor swing,
  which takes hours with tide and wind. Full-day data should push anchored drift *up* and widen the
  gap, so re-calibration against the full seven-day window is part of M2's definition of done.
- This validation independently confirmed the project's thesis. The five longest stops all reported
  `Under way using engine` while sitting at 0.0008–0.0098 nm of drift — squarely in the moored
  population — and transmitted every ~10 seconds, which is the *under-way* reporting rate rather than
  the 3-minute stationary rate. Drift geometry classified them correctly as berthed while the
  self-reported field was wrong. That belongs in the README.
- Phase classification reaches waiting-versus-working time **without a port boundary dataset**, from
  geometry and timestamps alone.
