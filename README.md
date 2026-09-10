# ais-pipeline

Turns the Danish Maritime Authority's open AIS feed into trustworthy per-vessel time series —
with provenance on every row, named quality rules, and ingestion you can run twice — then derives
when each tanker stopped, for how long, and whether it was waiting at anchor or working alongside.

That last distinction is the raw material of a laytime calculation.

> **Status:** in progress. M0 (foundations) complete — see [Milestones](#milestones).
> Figures below are measured on a 1.7M-row sample; full-window numbers land with M2.

## Why

AIS is a feed of self-reported messages from tens of thousands of transponders, relayed through
shore stations that overlap, duplicate and drop. Every number in it is an assertion by a device
nobody audits. Treating that as a database is how you end up confidently wrong.

So this project is built around a single rule: **a number you cannot trace is a number you cannot
trust.** Every stored record points back to the file and line it came from. Every rule that rejects
something says which rule, and keeps the evidence. Running the same file twice changes nothing.

## What was actually wrong with the data

Not what we expected — and that is the interesting part.

The original design anticipated the classic AIS sentinel values: speed of 102.3 kn, heading 511,
latitude 91 paired with longitude 181. Every one of those assumptions was checked against a real
file before a parser was written. Most were wrong:

| Expected | Measured on 1,717,280 real rows |
|---|---|
| SOG sentinel `102.3` | **Zero.** DMA blanks unavailable values instead — 146,612 blank SOG (8.5%) |
| Heading sentinel `511` | **Zero.** 438,778 blank (26%) |
| Latitude 91 / longitude **181** | Latitude `91.000000` / longitude **`0.000000`**, 7,013 rows. A `lon == 181` test catches nothing |
| Impossible speeds > 40 kn | **5 rows.** Maximum observed: 70.8 kn |
| Timestamps out of order per vessel | **Structurally impossible** — the file is globally time-sorted |
| Invalid MMSIs (not 9 digits) | 112,774 rows — all valid 7-digit base stations and 4-digit AtoN, not corruption |

The coordinates, speeds and identifiers in this feed are *clean*. What is actually wrong with it is
three things nobody warns you about:

**38.2% of the feed is duplicate.** 654,880 of 1,717,280 rows are exact duplicates on
`(mmsi, timestamp, latitude, longitude)` *within a single file*, because the DMA merges several
receiving stations. Deduplication is not a tidiness measure here; it is most of the work.

**63.1% of stationary tankers claim to be moving.** Of 11,710 tanker fixes below 0.5 kn, 7,387 report
navigational status `Under way using engine`. The vessel's own transponder contradicts the vessel's
own speed roughly two times in three. This project records the disagreement rather than picking a
winner.

**0.25% of rows break a naive comma split.** 4,261 rows carry quoted vessel names containing commas.
Split on `,` and every subsequent column shifts — speed read from the course field, ship type from
the name field. Parsed with a conformant CSV reader, exactly **1** row is malformed.

## Design

Four ideas, each with a decision record behind it:

- **Provenance.** Every normalized record references the ingest run and source line it came from.
- **Quarantine versus flag.** A rule either *rejects* a row — which quarantines it with the rule id
  and raw text — or *flags* it, keeping the observation and the doubt together. Nothing is dropped
  silently. ([ADR-0006](docs/adr/0006-quarantine-versus-flag.md))
- **Idempotent ingest.** A natural key on `(mmsi, ts_utc, lat, lon)` with `INSERT OR IGNORE`. Run the
  same file twice and the second run inserts zero.
  ([ADR-0005](docs/adr/0005-idempotent-ingest-natural-key.md))
- **Two sources, one truth.** Where a vessel's self-reported status disagrees with what its own speed
  says it was doing, the disagreement is recorded as data.

Architecture is a **modular monolith with a hexagonal core** — the domain has no I/O dependency at
all, which is what keeps every quality rule a pure function with a unit test. This is enforced
structurally: the unit test project references only `AisPipeline.Core` and cannot reach a database
even by accident. ([ADR-0002](docs/adr/0002-modular-monolith-over-microservices.md),
[ADR-0003](docs/adr/0003-hexagonal-architecture.md))

Notably, there is **no event sourcing** — because the property it provides is already here.
`position_report` *is* an immutable append-only event log with provenance, and `stop_event` and
`port_call` *are* projections rebuilt from it by `detect`.
([ADR-0009](docs/adr/0009-no-event-sourcing.md))

## Stop detection

A state machine over one vessel's fixes, read from the store in time order:

- Enter STOPPED below **0.5 kn**, leave at **1.0 kn**. The gap is hysteresis, so a vessel bobbing
  around half a knot yields one stop rather than fifty.
- A **coverage gap over 60 minutes breaks the stop.** A vessel that leaves receiver range simply
  stops appearing; without this, it would be recorded as stationary for days it may have spent
  steaming. ([ADR-0011](docs/adr/0011-coverage-gaps-break-stops.md))
- Stops touching a gap or the edge of the dataset are marked `is_complete = 0`. **Duration is only
  meaningful when it is 1.** Measured on the sample, 40 of 130 tankers were stationary across the
  *entire* observation window — censored stops are the common case, not the exception.

## Port calls: waiting versus working

Consecutive nearby stops chain into one port call. Each phase is classified by how far the vessel
drifted from its own centroid — a ship held by mooring lines barely moves; a ship on an anchor chain
swings with tide and wind.

The threshold was **calibrated, not guessed**, using each vessel's own reported status as a
semi-independent label:

| Threshold | ≈ metres | Accuracy |
|---|---|---|
| **0.010 nm** | **19** | **92.1%** |
| 0.030 nm | 56 | 73.7% |
| 0.300 nm | 556 | 52.6% |

The originally assumed 0.3 nm is wrong by a factor of thirty and performs worse than a coin flip.
Moored vessels sit at a median drift of 3.9 m, anchored ones at 55 m — the populations separate
cleanly, just far more finely than expected.
([ADR-0020](docs/adr/0020-berth-drift-threshold.md))

This reaches waiting-versus-working time **without a port boundary dataset**, from geometry and
timestamps alone.

The calibration also validated the project's central claim by accident: the five longest stops in the
sample all reported `Under way using engine` while drifting 0.0008–0.0098 nm — squarely in the moored
population. Drift geometry classified them correctly while the self-reported field was wrong.

## Data source

Daily CSV dumps from the Danish Maritime Authority, free to download.

```bash
# The archive moved: web.ais.dk now serves an expired certificate.
curl -O http://aisdata.ais.dk.s3.eu-central-1.amazonaws.com/aisdk-2026-09-05.zip
```

One day is ~590–750 MB zipped, ~3.0 GB expanded, ~17M rows, of which ~1.4M are tankers across ~300
vessels. Files are published with roughly a three-day lag. `data/` is gitignored; the committed
[`fixtures/sample.csv`](fixtures/sample.csv) holds 684 real rows chosen to exercise every rule —
see [the fixture manifest](fixtures/MANIFEST.md).

## Running it

*(CLI lands with M1.)*

```bash
ais ingest data/aisdk-2026-09-05.zip     # two-pass, idempotent
ais detect                               # stops + port calls, recomputed in place
ais quality                              # rule hit counts, per rule, per file
ais stops --min-hours 6 --complete-only
ais portcalls --min-waiting-hours 6
```

## Milestones

| | | |
|---|---|---|
| **M0** | Foundations — solution, CI, ADRs, fixture | ✅ complete |
| M1 | Ingest — parser, rules R1–R6, schema, idempotency | next |
| M2 | Detection — annotate pass, stops, port calls, seven days | |
| M3 | Postgres behind the same ports, Docker Compose | |
| M4 | REST + OpenAPI, GraphQL with DataLoader | |
| M5 | OpenTelemetry → Prometheus + Grafana, k6 | |
| M6 | Laytime engine | |
| M7 | Statement of Facts ingestion and discrepancy report | |

## Where this is going

A stop centroid is currently a coordinate. Resolved against a port dataset it becomes a berth; and
once arrival and departure resolve to berths, they are the inputs a charter party uses to count
laytime and calculate demurrage.

A Statement of Facts records what the parties agreed happened. AIS records what the ship's own
transponder says happened. **Comparing the two is the interesting part** — demurrage disputes turn on
contested timelines, and an independent record of where the vessel physically was is usually the
missing evidence.

To be clear about the boundary: **AIS cannot compute demurrage.** Notice of Readiness tendering,
hoses on and off, free pratique and the charter party terms are documents, not physical events. What
AIS provides is an independently verifiable timeline to check the Statement of Facts against.

## Limitations

- Decoded daily CSVs only — no raw NMEA decoding. Streaming ingestion would be the real-world version.
- No port boundary dataset yet, so stops are coordinates rather than places.
- Drift and gap thresholds are heuristics, calibrated on a sample and re-calibrated at M2.
- MMSI is not a durable vessel identity; it can be reassigned between voyages.
- Scoped to tankers. The quality report covers the whole feed; the store holds tankers only.

## Architecture decisions

Fifteen decisions are recorded in [`docs/adr/`](docs/adr/), including the ones where the answer was
*no*: why not microservices, why not MongoDB, why not event sourcing, and why the redundant index was
deleted rather than justified.

## Licence

MIT. AIS data © Danish Maritime Authority, free for download.
