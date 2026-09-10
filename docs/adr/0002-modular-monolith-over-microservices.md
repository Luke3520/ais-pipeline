# 2. Modular monolith over microservices

Date: 2026-09-11

## Status

Accepted

## Context

The pipeline ingests daily CSV dumps of AIS data, applies quality rules, stores normalized position
reports, and derives stop events and port calls. This is one batch data flow, one database, one
process. Roughly 17M rows per daily file, ~1.4M of them tankers.

The obvious alternatives were considered:

- **Microservices** would introduce network partitions, service discovery and eventual consistency
  in order to solve a coordination problem that does not exist here.
- **Serverless** fights the workload directly: ~21 GB of sequential parsing across a seven-day
  window, and the stop detector carries ordered per-vessel state across the whole stream.
- **N-Tier** is workable but biases toward the database leaking upward, with rules written against
  table shapes. For a project whose thesis *is* the domain rules, that is the wrong bias.

There is a real cost to this choice: a batch console pipeline demonstrates none of the distributed
machinery a larger system would use. That cost is accepted deliberately. Architecture is judged by
fit rather than ambition, and wrapping an orchestrator around this workload would demonstrate an
inability to size a solution.

## Decision

Build a modular monolith with four internal modules — Ingestion, Quality, Detection, Query — and
draw the module seams where a distributed version would cut.

The rule for when that changes: **batch → monolith; live stream → distributed.** A live AIS feed
would genuinely justify a broker, backpressure, queue-depth autoscaling and server-push. Daily CSV
dumps justify none of it.

## Consequences

- Ingest is a single transaction boundary; idempotency is enforced by a database constraint rather
  than by coordination between services (see ADR-0005).
- Debugging and testing stay local, which matters when the parser is the component most likely to be
  wrong.
- The distributed variant remains reachable rather than hypothetical: it attaches at the
  `IAisSource` port (ADR-0003), so a streaming source is an adapter rather than a rewrite.
- The README must state this as a decision with reasoning, or a reader will read the absence of
  distribution as an absence of consideration.
