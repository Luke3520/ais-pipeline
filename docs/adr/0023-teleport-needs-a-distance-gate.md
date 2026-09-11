# 23. The teleport rule needs a distance gate, not just a speed threshold

Date: 2026-09-11

## Status

Accepted

## Context

R7 was specified as "implied speed between consecutive fixes > 50 kn". That is the obvious shape
for a teleport rule and it is wrong on this feed.

Measured over all 746,284 consecutive fix pairs in `aisdk-2026-09-05`:

| Minimum distance | > 50 kn | > 100 kn | > 500 kn |
|---|---|---|---|
| none (speed only) | **2,084** | 861 | 42 |
| 0.05 nm | 280 | 156 | 42 |
| 0.10 nm | 100 | 62 | 42 |
| **0.50 nm** | **48** | 45 | 40 |
| 1.00 nm | 44 | 42 | 37 |

A speed-only rule at 50 kn fires on 2,084 pairs, and **87% of them moved less than 100 metres**
(median 60 m, p90 121 m). Class A transponders report every 2–10 seconds under way, and 60 m of
ordinary GPS scatter across a 2-second interval implies 55 kn. Those are not teleports; they are
a stationary vessel's position wobbling.

This matters far beyond noise in a report. ADR-0021 requires detection to **exclude R7-flagged
fixes from centroid and drift computation**. A speed-only rule would therefore drop roughly 1,800
good fixes per day out of exactly the drift calculation that separates a berth from an anchorage
— corrupting the M2 deliverable in the name of protecting it.

The genuine teleports are unambiguous and large: 143 pairs exceed 2 nm between consecutive fixes,
topping out at MMSI 266473000 jumping **3,040.9 nm in 10 seconds** to roughly 14°N 9°E and back,
three times in one day.

## Decision

**R7 fires when implied speed > 50 kn AND distance > 0.5 nm.** Both are named constants.

The distance gate is the load-bearing half. It encodes the physical claim that matters: a fix is
only suspect if the vessel appears to have *gone somewhere*, and half a nautical mile is far
enough outside GPS scatter that no stationary vessel reaches it.

`Haversine.ImpliedSpeedKn` returns null when no time elapsed, so pairs sharing a vessel-second —
2,975 of them, 0.40% of all pairs — are skipped rather than dividing by zero.

## Consequences

- 48 pairs a day are flagged instead of 2,084, and the flagged ones are real.
- Drift and centroid keep the fixes they should, so berth classification stays sound.
- The gate is a heuristic like every other threshold here: named, tunable, and stated in the
  README as recording evidence rather than establishing fact.
- A slow-moving corruption — a fix displaced 0.3 nm — is not caught by R7. R11 (reported speed
  versus implied speed) is the rule that covers that class, which is why both exist.
- This decision came from measuring before implementing, which is the method the rule set was
  rewritten under. Writing R7 from the plan would have shipped the wrong rule.
