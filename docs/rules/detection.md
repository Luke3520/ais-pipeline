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

**Berth is `max_drift_nm < 0.01` (≈19 m).** This was calibrated, not chosen: measured against the
vessels' own reported status as a semi-independent label, 0.01 nm separates moored from anchored at
92.1% accuracy, while the originally assumed 0.3 nm scores 52.6% — worse than a coin flip
(ADR-0020).

It is a **conservative floor.** The calibration window captured only a partial arc of an anchor
swing, so full-window data should push anchored drift up. Re-calibrate with the method in ADR-0020
rather than adjusting it by feel.

## Presenting thresholds

Drift and gap thresholds record evidence; they do not classify berths. The README says so, and any
output that presents a phase as a fact rather than an inference is overstating what the data
supports.
