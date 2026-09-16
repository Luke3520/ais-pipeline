# 45. Prune whole periods, and archive before deleting

Date: 2026-09-16

## Status

Accepted

Supersedes [ADR-0044](0044-retention-and-what-it-collides-with.md), which rejected retention after
the design it tried failed four ways.

## Context

ADR-0044 wanted raw fixes pruned on a 90-day window while the derived layer was kept forever, and
found that impossible: `position_report` is an immutable log, the derived layer is a total function
of it (rule 5), and a store holding derived rows over a partly-pruned log is a contradiction. Every
seam between the rebuilt half and the frozen half produced a defect — two foreign keys, a unique
constraint, and detection fabricating stops out of the anchor rows kept to satisfy the first foreign
key. The remedy for the last, never cutting through a port call, removed 7 fixes instead of 3.7
million because 58 of 362 calls span any given cutoff.

That record ended by pointing at a different design, and got one thing wrong on the way. It claimed
the export was "already a versioned archive of exactly the thing worth keeping". It is not: the
export carries the manifest, the rule counts and the per-vessel lapse analysis, and **no port
calls at all**. Pruning against it would have destroyed the history it was supposed to preserve.

## Decision

**Prune drops every projection along with the old fixes.** Not the projections for the pruned
period — all of them. They are a total function of the log, `detect` rebuilds them in seconds, and
dropping the lot removes every ordering question, every dangling reference and every collision at
once. What remains in the store is derived from what remains in the store, which is what rule 5
says and what the previous design could not maintain.

**The cutoff needs no adjustment.** Nothing is preserved across it, so nothing can straddle it. A
call that began before the cutoff and ended after is simply rebuilt from the fixes that survive, and
comes back truncated and **marked incomplete** — which is true, and is the same censoring the window
edge already applies (ADR-0011). Measured on the real store: 48 of the 50 calls at the new edge are
correctly incomplete.

**Prune writes its own archive, before it deletes anything.** A JSON file per prune holding every
port call that will no longer be derivable, with its phases, stops, hours and attributed port, plus
the source files that produced them. Written and verified non-empty first; if the archive fails, the
prune does not happen. This is the piece ADR-0044 assumed existed and did not.

**`--force` is required, and the dry run says what will go.** Destroying the only table that cannot
be recomputed should not follow from a mistyped command.

**`retention_event` records the cutoff, the counts and the archive path**, in the same transaction
as the deletion. Rule 2 is about rows not disappearing silently, and a deliberate deletion is still
a disappearance; without the path, a reader of a pruned store cannot find what used to be there.

## Consequences

Verified on the real store rather than the fixture, which is what the previous attempt lacked: 222
calls archived to 275 KB, 3.29M fixes and every projection removed, `detect` rebuilding 346 stops
and 193 calls, and re-running it landing on the same numbers. None of the four earlier failures
recur, because none of them has anywhere to occur.

**Old port calls leave the database.** They are in the archive, queryable as JSON but not as SQL —
no joins, no benchmark over them, no API. That is the price, and it is the right one: the previous
design's attempt to keep them live is exactly what could not be made correct.

**Benchmarks and lapse rates shift after a prune**, because they are computed over what the store
holds. A port's median waiting time is a statement about the retained window, which is why the
export manifest publishes that window and the site prints it.

**Re-deriving pruned history means re-ingesting the source files.** ADR-0024 and ADR-0025 each moved
a threshold and re-ran detection over everything; past the window that now needs the zips back
first. A project still tuning thresholds should keep a longer window than the time bar demands, and
buying disk stays the cheaper answer until roughly 30 GB.

**Nothing prunes automatically.** A scheduled job quietly deleting the only irreplaceable table is
not something to build before anyone has needed it once.

The tests go at the shape that broke the last design — a cutoff straddling the longest port call —
and assert that it straddles, so they cannot quietly stop covering it. That is the lesson ADR-0044
recorded: a fixture covers the shapes its author anticipated, and the previous eight tests passed
throughout while the real store failed four ways.
