# 24. The berth threshold scales with stop duration

Date: 2026-09-11

## Status

Accepted. Supersedes [ADR-0020](0020-berth-drift-threshold.md).

## Context

ADR-0020 set the berth threshold at a fixed `max_drift_nm < 0.01`, calibrated against a 2h40m
sample where it separated self-reported "Moored" from "At anchor" at 92.1%. It recorded that
figure as a **conservative floor** and required re-calibration against the full seven-day window,
which is what this record does.

The prediction was right and the model was wrong. Over 5,368,195 fixes and 809 stops spanning
2026-09-01 to 2026-09-07, **a moored vessel's measured drift grows with how long it sits there**:

| Stop duration | Moored median drift | At anchor median drift |
|---|---|---|
| 0–2 h | 0.0013 nm | 0.0197 nm |
| 2–6 h | 0.0146 nm | 0.0530 nm |
| 6–12 h | 0.0212 nm | 0.0810 nm |
| 24–72 h | 0.0261 nm | 0.1202 nm |
| 72 h+ | 0.0346 nm | 0.1414 nm |

A moored ship does not wander 60 metres down the quay. What grows is *apparent* position: GPS
error is a random walk, and the maximum excursion of a random walk accumulates roughly as the
square root of elapsed time. `max_drift_nm` is a maximum over fixes, so a longer stop has more
draws and a larger extreme — for reasons that have nothing to do with how the vessel is held.

No fixed threshold can track that. The best fixed value on seven days is 0.03 nm at 79.2%, and
the symptom in the output is a vessel oscillating across the line: one tanker produced eleven
alternating `Berth → Anchorage → Berth` phases in a single port call while sitting at one quay.

## Decision

Classify by **drift normalised by the square root of stop duration**:

```
berth  when  max_drift_nm / sqrt(duration_hours) < 0.008
```

Measured across the same 466 labelled stops:

| Score | Best threshold | Accuracy |
|---|---|---|
| `drift` (fixed, ADR-0020) | 0.030 | 79.2% |
| `drift / hours` | 0.0018 | 81.3% |
| **`drift / sqrt(hours)`** | **0.008** | **84.1%** |
| `drift / hours^0.25` | 0.0135 | 81.3% |

Validated out of sample rather than only fitted:

| Holdout | Fixed | Normalised |
|---|---|---|
| Fit on 01–04, test on 05–07 | 77.4% | **81.5%** |
| Fit on even MMSIs, test on odd | 92.6% | **96.3%** |

The threshold sits on a plateau — 83.3% at 0.007, 84.1% at 0.008, 83.9% at 0.009 — so it is not
balanced on a knife edge.

## Consequences

- Phase classification stops depending on how long the vessel happened to stay, which is what
  produced the alternating phases within one port call.
- **84% is not 92%, and the drop is honest rather than a regression.** The 92.1% figure came
  from short stops in a 2h40m window, which is the easiest possible case. Seven days includes
  long stops where the populations genuinely overlap.
- **The label is imperfect and the ceiling is not 100%.** Calibration uses the vessel's own
  reported status, and this project exists because that field is unreliable — R10 finds it
  contradicting the vessel's own speed on 37% of stops. The calibration set is restricted to
  stops where status and speed *agree*, which is the more trustworthy subset, but a crew that
  leaves "Moored" set while at anchor is mislabelled and no threshold can recover it. This is
  the best available proxy, not ground truth, and the README must say so.
- A better signal exists and is not used yet: an anchored vessel's swing is bounded by chain
  length and traces an arc, while GPS wander is isotropic. The *shape* of the fix cloud would
  separate them more sharply than its extent. That needs a port dataset or a shape statistic,
  and it is deferred rather than dismissed.
- ADR-0020's method stands; only its model changes. Re-calibrate the same way as the window grows.
