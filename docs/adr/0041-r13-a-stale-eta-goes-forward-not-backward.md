# 41. R13 — a stale ETA goes forward, not backward

Date: 2026-09-13

## Status

Accepted

## Context

ADR-0040 made destination and ETA available. Both are hand-entered, like the navigational status
whose staleness R10 and R12 already detect, and the obvious rule looked obvious: **flag an ETA in
the past.** A vessel still advertising an arrival time that has been and gone has clearly not
updated it.

Measuring first killed that rule, twice over.

**An ETA in the past barely happens.** Of 120,225 fixes carrying one, 1,192 were in the past and the
furthest by six hours. Nothing was days late.

**And it would not be a defect if it did.** An ETA is a *forecast*, not a statement about the
present. A navigational status of "under way" on a stationary ship is a false claim about what is
happening now; a passed ETA is a prediction that did not come true, which happens to every
prediction. Flagging lateness as a data-quality defect would be wrong on the merits and would bury
the real finding underneath it.

The real finding is in the other direction, and the AIS encoding explains it. **The ETA field has no
year.** ITU-R M.1371 gives it month, day, hour and minute — four fields, nothing more — so a decoder
meeting a date that has already passed must assume the next occurrence of it. A forgotten ETA does
not drift into the past. It is rolled forward and reappears up to a year ahead.

The distribution shows it with unusual clarity. Days between a fix and the ETA it carried:

| days ahead | fixes |
|---|---|
| under 14 | 95,075 |
| 14–30 | 12,526 |
| **30–60** | **0** |
| **60–90** | **0** |
| 90–180 | 911 |
| 180–300 | 1,679 |
| 300–340 | 628 |
| over 340 | 8,213 |

Two populations with **nothing whatsoever between them**, and the far one clustered hard at about a
year — the roll-over signature. Sampled dates land on 2027-08-27, 2027-08-30 and 2027-08-31 against
fixes taken on 2026-09-01.

## Decision

**R13 flags an ETA more than 60 days ahead of the fix carrying it.** Flags, never rejects: the
position on such a row is measured and sound, and only the typed field is stale.

**R13 does not flag an ETA in the past.** Lateness is not a defect, and the rule says so where
someone will read it.

**The threshold is 60 days, and its exact value provably does not matter.** The observed gap between
30 and 90 days is *empty* — not sparse, empty — so every threshold in that range classifies exactly
the same rows. This is a stronger footing than any other constant in this project: ADR-0020's berth
distance was a guess that later moved by a factor of thirty, and R12's sits in a trough that is
shallow rather than empty. 60 is the middle of the empty band.

## Consequences

A stale ETA is now countable, and `ais quality` reports it without further wiring because the report
is built from the registry (ADR-0032).

**The figures in this record come from a three-million-line slice of one day**, which is the window
in which the new columns existed when the rule was written. They are re-measured against the full
seven days when the store is rebuilt, and this record is wrong if the empty band turns out to be an
artifact of the slice. The claim to check is the gap, not the counts.

The rule cannot distinguish a stale ETA from a genuine plan more than two months out — a vessel
laying up for a season, say. Both are flagged. The distribution says such plans are absent from this
data rather than merely rare, but "absent from seven days of Danish tanker traffic" is a narrower
claim than "impossible", and a longer window may populate the empty band and force this to move.

Destination remains unchecked. It is free text mixing port names, UN/LOCODEs, route notation
(`FI SKV>DK LIN`) and non-destinations (`FOR ORDERS`), and matching it to the port a vessel actually
called at is a normalisation problem rather than a threshold problem. That is a separate record.
