# 29. When to revisit the architecture

Date: 2026-09-11

## Status

Accepted

## Context

ADR-0002 chose a modular monolith and stated the condition under which that changes: *batch →
monolith; live stream → distributed*. That is one trigger, and it is not the only one.

Architecture rarely fails at a decision point. It fails by drifting — one milestone adds a browser,
the next adds a write, the next adds a second user, and the shape that was right for a batch
pipeline is quietly carrying a product it was never sized for. Nobody decided that; it accumulated.

The project already has concrete plans that cross these lines: a map (Leaflet or Mapbox), and
further features beyond it.

## Decision

The current architecture is **provisional in named ways**. Crossing any trigger below re-opens the
conversation **before** code is written, not after.

| Trigger | What it changes |
|---|---|
| **A browser UI** (map, dashboard) | Static asset hosting, CORS, and a client that wants positions at map scale. 5.4M fixes cannot go down a wire as JSON — that means clustering, tiling, or vector tiles, which is a genuinely different read path from anything here now. Playwright becomes worth its place (ADR-0027 deferred it precisely because there was no UI). |
| **A write surface** | Uploading a Statement of Facts or charter party terms. Authentication stops being deferrable the moment commercially sensitive data arrives (ADR-0017), and the read/write split stops being a convenience. |
| **More than one user's data** | Tenant isolation, which this codebase has no concept of. Every query would need scoping, and a missing filter becomes a data breach rather than a bug — the threat model the provenance lane replaced (ADR-0022) comes back and the security lane returns with it. |
| **A live feed** | The ADR-0002 trigger. Broker, backpressure, autoscaling, SSE. Also breaks idempotency-by-natural-key and projection-rebuild, which are load-bearing here (see §Consequences). |
| **Scale past a full ordered scan** | Both the annotate pass and detection read every fix in `(mmsi, ts_utc)` order. That is fine at 5.4M and will not be at 500M. TimescaleDB's hypertable was measured and declined for the current shape (ADR-0026); this is the condition that revisits it. |

Until one is crossed, the shape stands and the answer to "should we restructure?" is no.

## Consequences

- The triggers are checked at the **start** of a milestone. Discovering one halfway through is how
  drift happens.
- **Streaming is not a near-term option and would cost real guarantees.** The DMA archive publishes
  with a measured three-day lag — `aisdk-2026-09-08.zip` appeared on 2026-09-11 — so there is no
  live variant of this source. More importantly, idempotency here is one UNIQUE constraint plus
  `INSERT OR IGNORE`, which works because batch is replayable, and thresholds get re-run as
  projections over an immutable log (done twice already: ADR-0024 superseded ADR-0020, ADR-0023 and
  0025 changed rules). A stream trades both for dedup windows and watermarks, and the commercial
  destination is retrospective anyway — demurrage is claimed after the voyage, against a 90-day
  time bar.
- A map is the most likely next trigger, and the one that changes most. It should be designed as a
  read path, not bolted onto the existing API.
