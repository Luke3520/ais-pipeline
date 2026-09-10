# 9. No event sourcing

Date: 2026-09-11

## Status

Accepted

## Context

AIS data looks like a textbook case for event sourcing: an immutable, timestamped stream of
observations from which current state is derived. Stop events and port calls are derived state, and
the ability to rebuild them from scratch when the derivation logic changes is genuinely valuable —
the detection thresholds are heuristics that will be tuned (ADR-0010, ADR-0020).

The temptation is to add an event store, projection handlers and replay machinery.

## Decision

No event sourcing. The valuable property is already present.

`position_report` **is** an immutable, append-only, timestamped log of events, each carrying
provenance back to a source file and line. `stop_event` and `port_call` **are** projections over it —
which is exactly why the `detect` verb deletes and recomputes them for affected vessels inside one
transaction.

The rebuild-from-truth guarantee that event sourcing exists to provide falls out of the domain for
free. Adding an event store on top would be event-sourcing something that is already events.

## Consequences

- Changing a detection threshold means re-running `detect`, not migrating an event store.
- Re-detection is idempotent by construction, enforced by `UNIQUE (mmsi, started_utc)` on
  `stop_event` and asserted by an integration test.
- Any future store — a graph projection of the voyage network, for instance — is added the same way:
  as another projection rebuilt from the same source of truth (ADR-0004).
- The README should make this observation explicitly. Recognising that a pattern is already present
  is worth more than importing its machinery.
