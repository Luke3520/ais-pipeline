# 48. Reported draught corroborates the berth, it does not prove it

Date: 2026-09-21

## Status

Accepted

## Context

The pipeline decides that a vessel worked alongside from drift geometry alone: how far it moved,
normalised by how long it stayed (ADR-0024). That is one signal, derived from measured position,
and until now nothing else in the feed spoke to the same event.

The AIS voyage message carries a **maximum present static draught**, and the store has held it
since ADR-0040 without anything reading it. A tanker that arrives at 12.4 m and leaves at 7.1 m has
discharged; the field says so directly, by a route that has nothing to do with geometry.

Measured on the seven-day window, across the 180 complete calls reporting a draught at both ends:

| The geometry says | reported a draught change | of | share |
|---|---|---|---|
| **worked alongside** | 33 | 66 | **50.0%** |
| **anchorage only** | 20 | 114 | **17.5%** |

A berthed call is **2.85 times** likelier to report a cargo change. The two signals agree far
better than chance, which is the same shape of corroboration ADR-0034 found between port distance
and drift geometry (59.7% against 16.8%).

## Decision

**`stop_event` stores the first and last draught reported during the stop, and `PortCall` derives
a `ReportedCargoMovement` from them.** Both are projections rebuilt by `detect`, so rule 5 holds:
nothing is edited in place and re-running detection reproduces them.

**The threshold is 0.5 m, and it comes from how crews round rather than from what it produces.**
Draught is typed, and 12.0, 12.5 and 13.0 are far commoner than 12.3. A threshold under half a
metre would read a crew's rounding as a cargo operation. It is a heuristic; it was not tuned
against the agreement figure above, and moving it would change that figure.

**The field is named `ReportedCargoMovement` and not `CargoMovement`.** This is the whole decision.
Draught arrives in the same hand-typed voyage message as the destination and the ETA — the two
fields this project has already shown to be the least reliable thing in the feed. A draught change
is a crew's *report* that cargo moved, never an observation that it did. Every name, every column
heading and every sentence on the site says "reported", because a reader who forgets that is being
told something the data cannot support.

**It does not enter waiting, working or laytime.** Those come from measured position alone, and
they feed a commercial claim. Mixing a typed field into them would put a number resting on a crew's
diligence inside a figure someone argues money over. `ReportedCargoMovement` sits beside the
laytime statement as corroboration and never inside it.

**Four outcomes, and `Unknown` is not `Unchanged`.** A call reporting no draught at either end says
nothing; a call reporting the same draught twice says the crew believes nothing moved. Collapsing
those would turn a silence into a claim — the same distinction `ais quality` draws between a rule
that fired zero times and a rule that is absent.

**Only complete calls are counted.** An open call's ends are not its ends, so a draught difference
across them could be a vessel that sailed out of the ingested window mid-operation.

## Consequences

**The corroboration is weaker than it looks, and the site must say so.** "Independent signal"
overstates it: position is measured by a receiver and draught is typed by a person, so this
corroborates the berth inference exactly as far as crews bother to update a field. It is
independent of the *geometry*, not of human diligence. The 17.5% at anchorage is the visible cost
of that — some of it is real ship-to-ship transfer and lightering, and some is a crew updating the
field late, and a radio signal cannot separate them.

**Half the berthed calls report nothing.** 33 of 66 is agreement worth publishing and is nowhere
near proof. A call the geometry puts alongside with no reported draught change is not evidence the
vessel did not work; it is evidence nobody said.

**Existing SQLite databases gain the columns through `AddMissingColumns`, and existing stops read
null until `detect` runs again.** That is correct and not a migration gap: `stop_event` is a
projection, so re-running detection is the supported way to fill it, unlike `position_report`,
which cannot be recomputed from anything.

**This is the third hand-typed field the project has taken seriously**, after navigational status
(R10, R12) and the ETA (R13). The pattern is now hard to miss: the measured half of an AIS message
is dependable and the typed half is a document of human attention. That is not a complaint about
crews — it is the thing that makes checking one half against the other worth doing at all.
