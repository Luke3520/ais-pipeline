---
paths: src/AisPipeline.Core/Detection/**, tests/AisPipeline.Tests/Detection/**
---

# Stop detection and port calls

## Stops

A state machine over one vessel's fixes, read from the store in `(mmsi, ts_utc)` order.

- Enter STOPPED below **0.5 kn**, leave at **1.0 kn**. The gap is hysteresis: a vessel bobbing
  around half a knot must produce one stop, not fifty.
- Emit when the stopped period lasted **≥ 30 minutes**.
- **Unavailable speed is *unknown*, not stopped.** It neither enters nor exits the state. Treating
  a missing value as zero fabricates stops — blocking class 1.
- **A coverage gap over 60 minutes closes the stop** and sets `is_complete = 0`. A vessel that
  leaves receiver range simply stops appearing; without this it is recorded as stationary for days
  it may have spent steaming (ADR-0011).

`is_complete = 0` also applies at the edges of the dataset. **`duration_hours` is only meaningful
when `is_complete = 1`** — 40 of 130 sampled tankers were stationary across the entire observation
window, so censored stops are the common case rather than an edge case. Any figure quoting a
duration must filter on it, or it is a wrong number on a published path (blocking class 4).

## Port calls

Consecutive stops chain into one visit when centroids are within ~10 nm and the gap between them is
under ~12 h. Each phase is classified by `max_drift_nm`.

**Berth is `max_drift_nm / sqrt(duration_hours) < 0.008`.** Normalised by duration, not compared
raw, because a moored vessel's *measured* drift grows with how long it sits there — 0.0013 nm under
two hours against 0.0346 nm past seventy-two. A moored ship does not wander down the quay: GPS error
is a random walk whose maximum excursion accumulates as the square root of elapsed time, and
`max_drift_nm` is a maximum over fixes, so a longer stop simply gets more draws.

The earlier fixed threshold of 0.01 nm (ADR-0020) was calibrated on a 2h40m sample and is superseded
by ADR-0024. A fixed value peaks at 79.2% on seven days and makes one vessel at one quay alternate
Berth/Anchorage eleven times within a single port call. Normalised scores 84.1%, and 81.5% on days
it was not fitted to.

**The label is a proxy, not truth.** Calibration uses the vessel's own reported status — the field
this project exists to distrust. The calibration set is restricted to stops where status and speed
agree, which is the more trustworthy subset, but a crew leaving "Moored" set while at anchor is
mislabelled and no threshold recovers it. The ceiling here is well below 100%.

Re-calibrate with ADR-0024's method as the window grows, rather than adjusting by feel.

## When the geometry cannot be trusted

Detection excludes positionally unreliable fixes from centroid and drift (ADR-0021). That is only
safe while the excluded fixes are a minority: with one survivor the centroid **is** that fix, drift
computes to exactly zero, and the stop classifies as the most confident possible berth — on the
vessels most likely to have been drifting.

A stop therefore records `reliable_fix_count`, and `max_drift_nm` is meaningful only when
`geometry_trustworthy` is 1 (at least two reliable fixes, and at least half the stop's fixes).
Otherwise the phase is `Unknown`, counted as neither waiting nor working and surfaced as
`unclassified_hours`. Never fold it into either: that puts a number the data does not support into
a laytime calculation (ADR-0025).

## Presenting thresholds

Drift and gap thresholds record evidence; they do not classify berths. The README says so, and any
output that presents a phase as a fact rather than an inference is overstating what the data
supports.
