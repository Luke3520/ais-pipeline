# Architecture Decision Records

Decisions are recorded here in [Michael Nygard's format](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions):
**Title · Status · Context · Decision · Consequences**.

This is the same discipline the pipeline applies to its data. A position report that cannot be
traced back to a source file and line number is a number you cannot trust; an architecture you
cannot trace back to a reason is a structure you cannot safely change. ADRs are provenance for
the design.

## Rules

- **One decision per record.** Numbered sequentially, never renumbered.
- **Status** is `Proposed` → `Accepted` → `Superseded by ADR-NNNN`.
- **Records are never edited after acceptance and never deleted.** A reversed decision gets a new
  record that supersedes the old one. What was believed, and when, is the artifact.
- **Cite evidence.** Where a decision rests on measured data, the record carries the number and
  how it was obtained.
- **Record rejected alternatives with their reasoning.** Knowing why MongoDB was not used is worth
  more than a record adopting it.

## Index

| # | Title | Status |
|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted |
| [0002](0002-modular-monolith-over-microservices.md) | Modular monolith over microservices | Accepted |
| [0003](0003-hexagonal-architecture.md) | Hexagonal architecture with a no-I/O core | Accepted |
| [0004](0004-relational-source-of-truth.md) | Relational store as source of truth | Accepted |
| [0005](0005-idempotent-ingest-natural-key.md) | Idempotent ingest via a natural key | Accepted |
| [0006](0006-quarantine-versus-flag.md) | Quarantine versus flag | Accepted |
| [0007](0007-two-pass-ingest-vessel-identity.md) | Two-pass ingest filtered by vessel identity | Accepted |
| [0008](0008-real-csv-reader.md) | A real CSV reader, not string.Split | Accepted |
| [0009](0009-no-event-sourcing.md) | No event sourcing | Accepted |
| [0010](0010-stop-detection-thresholds.md) | Stop detection thresholds and hysteresis | Accepted |
| [0011](0011-coverage-gaps-break-stops.md) | Coverage gaps break stops | Accepted |
| [0012](0012-split-duplicate-counters.md) | Split duplicate counters | Accepted |
| [0013](0013-drop-redundant-index.md) | Drop the redundant vessel-time index | Accepted |
| [0017](0017-defer-authentication.md) | Defer authentication until there is something to protect | Accepted |
| 0014–0016, 0018–0019 | *Never written. Numbers reserved during planning for decisions that were recorded as 0026 and 0027 when they were actually made. Left as a gap rather than renumbered: ADR numbers are identifiers, and renumbering would break every citation pointing at them. `scripts/check-adr-citations.sh` fails the build if source cites a number that does not resolve.* | — |
| [0020](0020-berth-drift-threshold.md) | Berth/anchorage drift threshold, calibrated | Superseded by 0024 |
| [0021](0021-sog-consistency-and-rule-ordering.md) | SOG consistency rule and annotate-before-detect ordering | Accepted |
| [0023](0023-teleport-needs-a-distance-gate.md) | Teleport rule needs a distance gate | Accepted |
| [0024](0024-berth-threshold-scales-with-duration.md) | Berth threshold scales with stop duration | Accepted |
| [0025](0025-r11-distance-gate-and-geometry-trust.md) | R11 distance gate; a stop refuses to classify untrustworthy geometry | Accepted |
| [0026](0026-postgres-adapter-and-no-hypertable.md) | Postgres adapter behind the same ports; no hypertable | Accepted |
| [0027](0027-api-surface-and-n-plus-one.md) | REST for operations, GraphQL for analysis; N+1 proved by counting | Accepted |
| [0028](0028-capped-collections-must-be-detectable.md) | A capped collection must be detectable, and must keep the newest | Accepted |
| [0029](0029-when-to-revisit-the-architecture.md) | When to revisit the architecture | Accepted |
| [0030](0030-laytime-engine.md) | A laytime engine, and the boundary of what AIS can prove | Accepted |
| [0022](0022-three-lane-review-harness.md) | Three-lane review harness | Accepted |
