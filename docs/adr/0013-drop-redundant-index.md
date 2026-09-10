# 13. Drop the redundant vessel-time index

Date: 2026-09-11

## Status

Accepted

## Context

The dominant read pattern is a per-vessel scan over a time range: stop detection, the annotate pass,
and every `stats` or `stops` query walk one vessel's fixes in `(mmsi, ts_utc)` order. The obvious
response is `CREATE INDEX ix_position_vessel_time ON position_report (mmsi, ts_utc)`.

That index is redundant. `position_report` already carries `UNIQUE (mmsi, ts_utc, lat, lon)` for
idempotency (ADR-0005), and `(mmsi, ts_utc)` is a strict **prefix** of it. A B-tree index serves
prefix lookups, so the unique index already answers every query the extra index would.

Keeping both costs a second index write on every insert — on the order of 6M inserts for a seven-day
window — and buys nothing.

## Decision

Do not create `ix_position_vessel_time`. Rely on the prefix of the unique constraint.

Indexes that *are* created, each with a stated reason:

- `UNIQUE (mmsi, ts_utc, lat, lon)` on `position_report` — idempotency, and serves per-vessel
  time-range scans by prefix.
- `ix_quarantine_rule` on `quarantine (rule_id)` — the quality report groups by rule.
- `ix_stop_vessel_time` on `stop_event (mmsi, started_utc)` — port-call chaining walks a vessel's
  stops in order. This one is *not* redundant: `stop_event`'s unique key is `(mmsi, started_utc)`,
  so the index and the constraint would coincide, and it is declared explicitly for clarity.

## Consequences

- Insert throughput improves measurably on the hot path.
- The README's "why each index exists" section can justify every index present — which it could not
  have done with a redundant one in the schema.
- If the access pattern ever diverges from the unique key's prefix, this decision needs revisiting
  with a new ADR rather than an ad-hoc index.
