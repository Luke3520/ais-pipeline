# 3. Hexagonal architecture with a no-I/O core

Date: 2026-09-11

## Status

Accepted

## Context

A founding principle of this project is that every quality rule is a pure function with unit tests.
That principle only holds if the domain has no I/O dependency — the moment a rule needs a database
connection or a file handle to be exercised, its tests become integration tests and stop being run
on every change.

Two inbound entry points are planned (a batch CLI, and later an HTTP API) and two outbound
substitutions are already on the roadmap (SQLite → Postgres, and CSV files → a live NMEA stream).

## Decision

Adopt hexagonal architecture (ports and adapters).

`AisPipeline.Core` contains the domain records, the quality rules, the stop-detection state
machine, the port-call chainer, and the geodesy — and has **no I/O dependencies of any kind**. It
declares ports: `IAisSource`, `IPositionStore`, `IQuarantineStore`, `IVesselStore`.

Adapters implement those ports: `Adapters.Csv`, `Adapters.Sqlite`, later `Adapters.Postgres`.
The CLI and the API are inbound adapters over the same core.

**Acceptance test for the structure:** every quality rule and the state machine must be unit-testable
with no database and no files. This is enforced structurally — the `AisPipeline.Tests` project
references only `AisPipeline.Core`, so it cannot reach an adapter even by accident.

Onion architecture was considered and rejected as a duplicate vocabulary for the same idea; carrying
both terms buys nothing.

## Consequences

- The rule suite runs in milliseconds and is exercised on every commit.
- Swapping the store is an adapter change; the same test suite runs against both (M3 requires it).
- A live streaming source becomes an `IAisSource` implementation rather than a redesign, which is
  what keeps ADR-0002's "live stream → distributed" escape hatch real.
- Slight indirection cost at the adapter boundary, accepted.
