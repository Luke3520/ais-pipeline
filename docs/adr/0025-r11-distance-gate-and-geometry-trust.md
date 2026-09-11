# 25. R11 needs the same distance gate as R7, and a stop must refuse to guess

Date: 2026-09-11

## Status

Accepted

## Context

Two defects found while reviewing M2, each harmless alone and compounding badly together.

**R11 fired on GPS scatter.** Measured over seven days, R11 flagged 35,303 fixes — 0.66% of the
store — with a **median displacement of 57 metres over a median interval of 2 seconds**, and 87%
of hits moving under 100 m. Its hit distribution peaked on vessels reporting 10–11 kn, ordinary
service speed. This is exactly the defect ADR-0023 diagnosed for R7 and then fixed only for R7:
a rule comparing speeds without asking whether the vessel actually went anywhere.

**A stop whose fixes are mostly excluded reports drift of zero.** ADR-0021 has detection exclude
positionally unreliable fixes from centroid and drift, reasoning that one bad row must not poison
a whole event. That reasoning silently assumes the excluded fixes are a minority. They need not
be. With a single survivor the centroid **is** that fix, so `max_drift_nm` computes to exactly
`0.0`, and `0.0 / sqrt(hours) < 0.008` classifies the stop as a **berth** — the most confident
label available — on precisely the vessels R11 flagged for drifting while claiming to be moored.

Three stops in the seven-day window carried `max_drift_nm = 0.0`, and eight near-zero stops were
all labelled Berth. R11's over-firing is what would have turned that from rare into routine, since
it was marking fixes across 226 of 809 stops.

## Decision

**R11 gains a minimum distance of 0.1 nm**, alongside its existing speed-excess conditions.

Set deliberately below R7's 0.5 nm: R11 exists to catch movement too slow to trip a teleport
threshold, so it has to be the more sensitive of the two. Measured GPS scatter reaches 118 m at
the 90th percentile, so 0.1 nm (185 m) clears it. Hits fall from 35,303 to 1,562 — and the case
the rule was built for, a vessel reporting 0.0 kn while moving 11 nm, is untouched.

**A stop records how much of its geometry survived, and refuses to classify when too little did.**
`StopEvent` carries `ReliableFixCount`, and `GeometryTrustworthy` requires at least two reliable
fixes — the fewest from which a spread is measurable at all — and at least half the stop's fixes.
`PortCallChainer.Classify` returns the new `StopPhase.Unknown` when that fails.

Unknown phases are counted as **neither waiting nor working**, and surfaced as
`PortCall.UnclassifiedHours` rather than absorbed into either. Folding them into one would put a
number the data does not support into a laytime calculation.

## Consequences

- After the gate, no stop in the seven-day window has untrustworthy geometry: the lowest reliable
  share is 66.7% and the mean is 99.6%. **The guard is therefore dormant on this data**, like R6,
  which has never fired on a tanker. It is defence against a condition that was live until the
  R11 fix, and it is cheap.
- Three stops still report drift of exactly zero. Those are genuinely stationary vessels reporting
  an unchanging position, and they pass the trust check — which is the distinction the guard
  exists to draw: *no measurable spread* is not the same as *nothing left to measure*.
- `max_drift_nm` must be read together with `geometry_trustworthy`. The schema says so.
- Both defects were caught by the review harness rather than by tests or measurement, and both had
  the same shape: a rule or an aggregate that was correct on the common case and silently wrong on
  the case it most needed to handle.
- The general lesson is worth stating: **any rule comparing a reported value against one derived
  from positions needs a minimum displacement**, because AIS reporting intervals are short enough
  that GPS scatter dominates every rate computed across them. R7 and R11 now both have one; a
  future rule of that shape should start with one.
