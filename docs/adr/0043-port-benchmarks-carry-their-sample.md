# 43. A port benchmark carries the sample it rests on

Date: 2026-09-14

## Status

Accepted

## Context

"Is 40 hours at Skagen normal?" is the question an agent or an owner actually asks, and it is the
first thing this project can answer that is neither a dispute artefact nor a data-quality finding.
The derived layer already holds what it needs: 357 port calls attributed to 49 ports, each with
waiting and working hours.

The trap is that the answer looks like one number and is not. Three things stand between the stored
calls and a defensible median:

- **Incomplete calls.** A call whose true extent is unknown has hours that are lower bounds
  (ADR-0011). Averaging a bound into a median drags it down, and nothing in the output would say so.
  179 of 357 are incomplete on this window.
- **Calls attributed from too far off.** A call 14 nm from its nearest port is an anchorage in open
  water, not that port's call (ADR-0034). Another 59 go this way.
- **Sample size.** After both exclusions, 119 calls remain across 49 ports. Skagen has 33. Most
  ports have fewer than five.

357 becomes 119. A median drawn from a third of the input, published without saying so, is exactly
the kind of confident figure this project exists not to produce.

## Decision

**Every figure travels with its sample and with what was excluded.** `AttributedCalls`,
`ExcludedIncomplete`, `ExcludedTooFar` and `UsableCalls` are on the benchmark, and they sum:
every attributed call is accounted for by exactly one outcome, the same identity `IngestCounters`
applies to rows.

**Below five usable calls there is no median, and it is null rather than thin.** With four calls the
median is the mean of the middle two and one congested call moves it by hours. A p90 needs ten:
from nine observations it is the largest of nine, which is a maximum wearing a percentile's name.
Both minimums are stated, not tuned, and the count is always published so a reader can disagree.

**Percentiles are nearest-rank, never interpolated.** With samples this small, interpolation invents
a duration no vessel experienced, and the figure exists to describe calls that happened. The sorted
observations ship with the benchmark so the percentiles can be checked rather than trusted.

**The endpoint ranks rather than judges.** `GET /ports/{wpi}?waitingHours=40` answers "longer than
28 of 33 calls, the 85th percentile" — a position in a distribution. It does not say whether that
was reasonable, because that depends on the charter party, the berth and the cargo, none of which
AIS can see.

**The filtering happens in Core, not in a WHERE clause.** Deciding which calls may support a
benchmark is a judgement about evidence. In SQL it would be invisible and untested; in
`PortBenchmarkBuilder` it is both, and the exclusions can be counted.

## Consequences

Seven days is a short window to call anything normal. Skagen's 33 calls are the only sample here
that comfortably supports a median, and its distribution is visibly two populations — calls from
0.9 to 27.7 hours, then a jump to 42.6, 48.2, 60.9, 68.7 and 77.4. That tail is congestion, and
whether it is typical of Skagen or of one week in September this data cannot say. The window is
published with every response for exactly that reason.

The exclusion of incomplete calls biases the sample toward shorter visits: a call long enough to
span the edge of the ingested window is likelier to be truncated, so the longest calls are the ones
most often dropped. Extending the window is the only fix, and the bias should be assumed to
understate waiting times until it is measured.

Benchmarks are computed on demand from 357 rows rather than stored. At a hundred times this volume
it is still a trivial scan, and storing them would create a second thing to invalidate when
`detect` re-runs.
