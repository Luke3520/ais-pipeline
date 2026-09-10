# 6. Quarantine versus flag

Date: 2026-09-11

## Status

Accepted

## Context

Quality rules find two different kinds of problem, and treating them identically loses information
either way. A row with a sentinel latitude of 91 carries no usable position and cannot enter a time
series. A row whose speed is unavailable is still a valid, located observation — discarding it would
punch a hole in the vessel's track for a reason unrelated to where the vessel was.

Silently dropping either kind is the failure mode this project exists to avoid.

## Decision

Every rule declares one of two outcomes.

**Reject** → the row does not enter `position_report`. A `quarantine` row is written carrying the
rule id, the source file and line, the raw text, and a detail message. Nothing is dropped without
evidence.

**Flag** → the row enters `position_report` with the rule id appended to `quality_flags`. The
observation is kept and the doubt is kept with it, so downstream consumers can decide.

`quarantine` carries `UNIQUE (source_file, source_line, rule_id)`. Without it, re-ingesting a file
would leave `position_report` unchanged while quarantine rows doubled, breaking the idempotency
guarantee of ADR-0005 through a side door.

The one exception is R2, exact duplicates, which are counted rather than quarantined — at 38.2% of
the feed they would bury every genuine reject (ADR-0005).

## Consequences

- The pipeline can always report what it refused and why, at row granularity.
- `quality_flags` is a comma-joined string rather than a normalized table. A deliberate
  simplification: flags are read as a set for display and rarely queried individually. Revisit if
  that changes.
- Rejects are recoverable — the raw text is retained, so a rule fixed later can be re-applied to
  quarantined rows rather than requiring a full re-ingest.
