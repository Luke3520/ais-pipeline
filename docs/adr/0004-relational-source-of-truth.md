# 4. Relational store as source of truth

Date: 2026-09-11

## Status

Accepted

## Context

The data is four distinct shapes: `position_report` (millions of rows, uniform 26-field schema,
append-only, queried by vessel and time range — a time series); `vessel` (small, key-value by MMSI);
the derived hierarchical aggregates `stop_event` / `port_call` / `port_call_phase`; and the audit
logs `quarantine` / `ingest_run`.

Document and graph stores were both evaluated seriously rather than dismissed.

**MongoDB.** The honest case: the bucket pattern (one document per vessel-day with an embedded fix
array) turns "read a vessel's whole day" into a single document read, a port call with its phases is
naturally one nested aggregate, and native time-series collections are genuinely good. It loses on
three specific points. Idempotency degrades from a unique-index rejection to roughly 2.3M array
upserts per day, because 38.2% of the feed is duplicate (ADR-0005). Multi-document transactions
require a replica set, and batch ingest atomicity matters here. And the workload is overwhelmingly
analytical aggregation — rule hit counts grouped by rule and file, time-range scans, joins from stop
events to vessels — which is more painful in an aggregation pipeline than in SQL. Schema flexibility
solves a problem that a rigidly-schema'd 26-column feed does not have.

**Neo4j.** Storing millions of position fixes as nodes would be pathological; that is a time series,
not a graph. But once port calls exist, a *sequence* of port calls is a voyage, and voyages form a
network. Questions like "which tankers called at berth X within 30 days of calling at berth Y" are
variable-depth traversals where SQL joins get ugly and Cypher stays readable. That is a real graph
problem — it is simply not this milestone's problem.

## Decision

A relational store is the source of truth.

The decisive argument is that the `UNIQUE` natural key **is** the idempotency mechanism (ADR-0005).
Rejecting 38.2% duplicates at index level via `INSERT OR IGNORE` is the cheapest possible
implementation of this project's central guarantee, and it is a relational primitive.

Staged as: **SQLite** through M2 (zero operational cost, ideal for the ingest and detection loop),
then **Postgres** at M3, with TimescaleDB considered for the fix table as the window grows.

Additional stores, if they arrive, arrive as **projections** rebuilt from the relational source of
truth — architecturally identical to how `stop_event` is already built (ADR-0009). Neo4j for
voyage-network analysis, and MongoDB for parsed Statement of Facts and charter-party documents, where
variable clause structures genuinely are document-shaped.

## Consequences

- Idempotency, transactions and analytical aggregation are all served by one engine.
- Schema enforcement catches parser bugs at the storage boundary — valuable, because the parser is
  the component most likely to be wrong.
- SQL must be written through the store ports with no SQLite-specific syntax in Core, or the M3
  Postgres migration stops being an adapter swap.
- Polyglot persistence stays available but must be *earned*: a second store needs a query the
  relational store answers badly, not novelty.
