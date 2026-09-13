# 40. Store the rest of the feed

Date: 2026-09-13

## Status

Accepted

## Context

The DMA daily CSV ships **26 columns**. The parser read **15** and the store kept 14. Eleven were
never read at all, and nothing recorded that or why — `DmaColumns` defines a constant for all 26,
so a reader of the design would reasonably assume all 26 were in play.

They are not empty. Measured over 400,000 rows of one day:

| column | populated |
|---|---|
| Type of position fixing device | 78.3% |
| ROT | 69.9% |
| Destination | 60.8% |
| Draught | 59.7% |
| ETA | 53.2% |
| Cargo type | 15.2% |
| Data source type | 100% — and constant |

The omission mattered more than a missing column usually does, because of what this project turned
out to be about. `Destination` and `ETA` are **hand-entered**, exactly like the navigational status
whose staleness is the subject of the whole site. A vessel still broadcasting a destination it
reached two days ago is the same failure as a dial left on "under way"; the pipeline could not see
it because it never read the field.

## Decision

**Read and store six of the eleven: ROT, draught, destination, ETA, cargo type, position fixing
device.**

**Split by how they behave, not by which AIS message carries them.** Dynamic and voyage values —
ROT, draught, destination, ETA — go on `position_report`, because a rule needs to know what a
vessel was claiming *at a given moment* and the value changes within a voyage. Installation
properties — cargo type, position fixing device — go on `vessel`, resolved by the same two-pass
merge that already resolves name and ship type (ADR-0007).

**Stored per row, and the duplication is accepted.** Destination is broadcast on every position row
because the DMA feed flattens AIS's static and dynamic messages onto one line, and `position_report`
is the log of what the feed said. A deduplicated voyage table would be a *projection* over it and can
be built later by `detect` if anything needs one; inventing it now would put a derived table in
front of the log it derives from.

**Four columns are deliberately still not read**, and this record is where that is written down:

- `A`, `B`, `C`, `D` are antenna offsets from the hull reference point. DMA already derives `Length`
  and `Width` from them and we store those, so keeping the offsets as well stores the same fact
  twice in a less usable form. Worth revisiting only if a berth-scale position correction is ever
  needed.
- `Data source type` is `'AIS'` on every one of 200,000 sampled rows. A column with one value
  carries no information.

## Consequences

`position_report` grows by four columns over 5.4M rows — roughly 270 MB on an 822 MB database. That
is the price of keeping the feed's own record rather than a summary of it, and the provenance
guarantee already lets any unread column be recovered from the source file, so this is a
convenience rather than a rescue.

**Existing databases gain the columns but not the values.** The migration adds them by ALTER, and
every row ingested before this record reads null. Re-ingesting the same files will not fill them:
ingest is idempotent by natural key and skips the rows. A store that needs them has to be built
fresh, as with R12 (ADR-0038).

The bespoke port-column migration from ADR-0034 is replaced by a table of `(table, column, type)`
checked at `EnsureSchema`. Three schema additions in two days is enough to stop writing each one by
hand.

This unblocks two rules that could not exist before — a destination that outlived its voyage, and an
ETA in the past — and gives laytime a laden-versus-ballast signal it did not have.
