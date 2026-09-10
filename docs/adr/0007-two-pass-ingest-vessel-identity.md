# 7. Two-pass ingest filtered by vessel identity

Date: 2026-09-11

## Status

Accepted

## Context

The pipeline scopes to tankers to keep the working set tractable — roughly 1.4M rows a day out of
~17M. The obvious implementation is to filter each row on its `Ship type` column.

That implementation is wrong, and measurably so. In the DMA feed, static data (ship type, name, IMO,
dimensions) rides only on rows derived from AIS message type 5. Position-report rows carry
`Undefined`. Measured on the 1,717,280-row sample: **2,399 of 3,110 MMSIs emit more than one distinct
ship type**, and **121 of 130 tanker MMSIs also emit rows tagged `Undefined`**.

Filtering per row therefore discards **2.6% of each tanker's own position fixes** — not randomly, but
scattered through the track. Those are unprovenanced holes in exactly the time series stop detection
reads, and they widen gaps in a way that perturbs stop boundaries.

## Decision

Ingest each file in two passes.

**Pass 1 — identity and quality.** Stream every row. Build `MMSI → {ship_type, name, imo, callsign,
dimensions}`, latest-wins, into `vessel`. Evaluate the row-local quality rules across the *whole*
feed and record their hits, so the quality report covers all ~17M rows even though only tankers are
stored.

**Pass 2 — positions.** Stream again, keeping rows whose **MMSI** is in the tanker set — not rows
whose own text says "Tanker". Apply the rules and batch-insert.

## Consequences

- No tanker fix is lost to a missing static field.
- The `vessel` table becomes a first-class artifact built deliberately, rather than a by-product of
  whichever row happened to be seen last.
- Costs one extra sequential read of a local file, on the order of a minute. Accepted.
- The quality report describes the whole feed while the store holds only tankers. This must be stated
  plainly in the README or the numbers will not reconcile.
- Vessel dimensions come from Size A+B and C+D. Verified on the sample: `A+B == Length` on all
  1,453,172 rows carrying both, and those fields are present on 85% of rows versus 53% for IMO.
