# ais-pipeline

C#/.NET pipeline that turns the Danish Maritime Authority's open AIS feed into trustworthy
per-vessel time series, then derives stops and port calls. The point of the project is **trust in
the numbers**, not throughput.

## Rules (never break these)

1. **Provenance.** Every stored record points back to the ingest run and source line it came from.
   A number you cannot trace is a number you cannot trust.
2. **Nothing is dropped silently.** A rule either *rejects* a row — which writes a `quarantine` row
   carrying the rule id, source line and raw text — or *flags* it, keeping the row and the doubt
   together in `quality_flags`. There is no third option. A row that disappears without a
   quarantine record or a counter is a defect, not a filter.
3. **Ingest is idempotent.** Same file twice inserts zero the second time. The natural key
   `(mmsi, ts_utc, lat, lon)` is the mechanism. This must hold for `quarantine` too, not just
   `position_report`.
4. **Record disagreement, do not resolve it.** Where a vessel's self-reported status contradicts its
   own speed, store both and mark the conflict. Never silently pick a winner.
5. **Derived tables are projections.** `stop_event` and `port_call` are rebuilt from
   `position_report` by `detect`, never edited in place.

## Architecture (ADR-0002, ADR-0003)

Modular monolith, hexagonal. `AisPipeline.Core` has **no I/O dependencies at all** — no file, no
database, no network. Adapters implement its ports; the CLI and (later) the API are inbound
adapters over the same core.

`tests/AisPipeline.Tests` references only `Core`, which makes the rule a compiler constraint. If a
change makes that project need an adapter, the change is wrong, not the constraint.

No SQLite-specific SQL in Core — the Postgres adapter at M3 depends on it.

## Units (docs/rules/units-and-geodesy.md)

Distances in **nautical miles**, speeds in **knots**, times in **UTC**, 1 nm = 1852 m. Every
variable carrying a physical quantity names its unit (`max_drift_nm`, `sog_kn`, `duration_hours`).
Parse with `InvariantCulture` and an explicit format — this machine runs a Danish locale.

## Conventions

- `dotnet format` owns formatting. Warnings are errors (`Directory.Build.props`).
- Quality rules are pure functions in `Core/Quality`, one per rule id, each with unit tests.
- Thresholds are named constants, never inline literals. They are heuristics and the README says so.
- **Never round timestamps.** Laytime is counted to the minute and rounding compounds.

## Commands

```bash
./scripts/check.sh      # format + build + test. Runs pre-push and in CI.
dotnet test             # tests only
```

## Reviewing a change

`/review-pass [scope]` fans the diff to three read-only lanes and adjudicates the results.
`docs/rules/checks-and-review.md` defines what blocks. Non-blocking findings go to
`docs/review-followups.md` and are never dropped silently.

## ADRs

`docs/adr/`. **Accepted records are never edited and never deleted** — a reversed decision gets a
new record that supersedes the old one. Decisions resting on measurement carry the measurement.

## Git

Small commits with plain messages. The history is part of the artifact. No force-pushing `main`
once public. Do not add tool-attribution trailers to commit messages.

## Definition of done

A change is done when: `./scripts/check.sh` is green; domain changes have unit tests that assert
behaviour rather than restating the implementation; a new or changed quality rule has its counter
visible in `ais quality`; and any decision worth arguing about has an ADR.
