# 44. Retention: attempted, and rejected for now

Date: 2026-09-14

## Status

Accepted

## Context

The store only accumulates — nothing in it had ever deleted anything. Measured: 766,885 rows and
149 MB a day, so a year is 280M rows and 53 GB against 42 GB free. Disk binds at roughly nine
months, and before CPU does.

The commercial clock says what to keep. Demurrage is claimed retrospectively against a **90-day time
bar** (ADR-0029), so raw fixes are evidence for about that long and then stop being evidence. The
derived layer is the opposite: it is what anyone actually looks at, and it costs **25 KB a day**
against the raw log's 149 MB — a factor of six thousand. Keeping port calls forever is free.

So the policy seemed obvious: prune `position_report` on a 90-day window, keep everything derived
forever. It was implemented, and it does not work. This record exists because the reasons are not
obvious and the next person will have the same idea.

## What was built, and how it failed

A `prune` verb, a `retention_event` record, and a bound on detection so it could not destroy what it
could no longer recompute. Four distinct failure modes appeared, each revealed by fixing the last,
and all four in a single session.

**1. The foreign key refused the delete.** `stop_event.first_position_id` references
`position_report`. A fix a stop points at cannot be deleted at all — the schema enforces the
evidence pointer. *Fix: keep the endpoint fixes of surviving stops, two rows per stop.*

**2. Detection then fabricated stops from those anchors.** Detection reads every fix. Two isolated
rows per pruned stop, in the same place hours apart, look exactly like a stationary vessel. *Fix:
bound what detection reads, not only what it replaces.*

**3. A preserved port call's later stops were deleted.** A call beginning before the bound survives,
but a stop inside it can begin after the bound, and deleting that stop left the surviving call's
phase pointing at nothing. *Fix: keep any stop still referenced by a surviving phase.*

**4. Preserved stops were then re-detected, and collided.** Those same stops are rebuilt from fixes
that are still present, and the second copy violates `UNIQUE (mmsi, started_utc)`. The obvious
remedy — never cut through a port call, move the cutoff back to the spanning call's arrival —
**makes pruning a no-op on real data: 58 of 362 calls span any given cutoff**, so the boundary
retreats to the start of the window and 7 fixes are removed instead of 3.7 million. And it still
collided.

The pattern is the point. `position_report` is an immutable log and the derived layer is a total
function of it; rule 5 says so. Pruning creates a hybrid — part rebuilt, part frozen — and every
seam between the two halves produces a defect. Vessels are continuously present and port calls
overlap arbitrarily, so **there is no clean seam in this data to cut on**.

## Decision

**Do not prune. Revert the implementation.** A half-working destructive feature over the one table
that cannot be recomputed is worse than no feature, and this one was four patches deep and still
failing on the real store while passing on the fixture.

**Revisit at real disk pressure, not before.** One gigabyte is in use against 42 free. The trigger is
~30 GB, which is around six months of daily ingest.

**When it is revisited, the promising design is not this one.** The export already produces a
versioned JSON snapshot of the derived layer (ADR-0039), which is exactly "keep the derived data
forever" in a form that nothing rebuilds and nothing can collide with. That points at pruning
**whole periods** — raw fixes *and* their projections together, after the period's snapshot is
committed — rather than keeping a live derived layer over a partially-pruned log. It gives up
querying old port calls from the database in exchange for a seam that actually exists.

## Consequences

The store keeps growing at 149 MB a day and the nine-month horizon stands. That is a known,
measured, bounded problem, which is a better position than a silent one.

**Retention costs more than disk: it costs the ability to re-derive.** ADR-0024 and ADR-0025 each
moved a threshold and re-ran detection over all of history. Past any retention window that requires
re-ingesting the source files first. A project that expects to keep tuning thresholds should want a
longer window than the time bar alone demands, and that argues for buying disk over pruning.

**The fixture suite passed throughout while the real store failed.** Eight integration tests over
684 rows covered every case I could think of and none of the four failures above; each one needed
5.4M rows and 362 overlapping port calls to appear. Where a fixture cannot express the shape that
breaks something, running against the real store is not optional.

**The evidence pointer survived by being enforced.** Failure 1 was caught by a foreign key rather
than by review, which is an argument for constraints the database can check over invariants only a
comment asserts.
