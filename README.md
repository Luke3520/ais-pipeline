# ais-pipeline

Turns the Danish Maritime Authority's open AIS feed into trustworthy per-vessel time series —
with provenance on every row, named quality rules, and ingestion you can run twice — then derives
when each tanker stopped, for how long, and whether it was waiting at anchor or working alongside.

That last distinction is the raw material of a laytime calculation.

> **Status:** in progress. M2 (detection) complete — see [Milestones](#milestones).
> Seven days ingested, 809 stops and 362 port calls detected, waiting-versus-working time
> measured without a port dataset.

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

## One day, ingested

```
$ ais ingest data/aisdk-2026-09-05.zip --ship-type Tanker
run 1  aisdk-2026-09-05.zip  (56.9s)
  read 16,420,337  inserted 746,467  dup-in-file 597,005  dup-prior-run 0
  quarantined 14   filtered 15,076,851
  vessels in scope: 183
```

Those numbers add up exactly — `746,467 + 597,005 + 14 + 15,076,851 = 16,420,337` — and the
pipeline checks that identity itself, exiting non-zero if a single row read is not accounted for.
That is what "nothing is dropped silently" means in practice rather than in principle.

Run it a second time and it inserts **0**, with all 746,467 attributed to `dup-prior-run`.

**44% of one tanker's own fixes are duplicates of each other** — 597,005 against 746,467 unique —
because the feed merges receiving stations. And measured across the full day's 77,029 stationary
tanker fixes, **63.8% report `Under way using engine` while sitting still.** A 1.7M-row sample had
predicted 63.1%.

## Seven days, detected

```
$ ais detect
annotated 5,368,195 fixes
    R7    492     teleports
    R8    706     coverage gaps
    R11   1,562   reported speed contradicting implied speed
detected across 452 vessels (35.0s)
  stops 809  (complete 517)  port calls 362
  stops where the vessel's own status contradicted its speed: 299 (37.0%)
```

**517 of 809 stops are complete** — their true start and end are both inside the window. One day
alone would have yielded 30–50, because roughly a third of tankers are stationary across any given
midnight. That is the whole reason for a seven-day window: it is what turns a stop into a port call.

Running `detect` twice produces a byte-identical result. These tables are projections over
`position_report`, so changing a threshold means re-running, not migrating.

## Waiting versus working

```
636025106  STINGRAY         wait 30.4h   work 46.5h    Anchorage -> Berth
563045300  EAGLE BARCELONA  wait 12.4h   work 42.3h    Anchorage -> Berth
538006310  SEAGAS LOYALTY   wait 34.3h   work 23.2h    Anchorage -> Berth
353576000  MERSINI          wait 69.8h   work 34.4h    Anchorage -> Berth -> Anchorage
```

Thirty complete port calls carry both. No port boundary dataset was used: the split comes from
drift geometry and timestamps alone. Waiting at anchor and working alongside are the two quantities
a laytime calculation is built from.

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

The originally assumed 0.3 nm was wrong by a factor of thirty and performed worse than a coin flip.

Then seven days showed the *model* was wrong too. A moored vessel's measured drift grows with how
long it sits — 0.0013 nm under two hours against 0.0346 nm past seventy-two — because GPS error is
a random walk and `max_drift` is a maximum over fixes, so a longer stop simply gets more draws. No
fixed threshold tracks that, and the symptom was one vessel at one quay alternating
`Berth → Anchorage` eleven times in a single call. The classifier now normalises:
`max_drift_nm / sqrt(duration_hours) < 0.008`, which scores 84.1%, and **81.5% on days it was not
fitted to.** ([ADR-0020](docs/adr/0020-berth-drift-threshold.md), superseded by
[ADR-0024](docs/adr/0024-berth-threshold-scales-with-duration.md))

Worth stating plainly: the label is the vessel's own reported status — the very field this project
distrusts — so 84% is measured against an imperfect proxy and the ceiling is well below 100%.

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

## Working on it

```bash
git config core.hooksPath .githooks   # once per clone: enables the pre-push check
./scripts/check.sh                    # format, build, test -- same script CI runs
```

The hook path is local git config rather than something a clone inherits, so that first command is
the one step a fresh checkout needs. Without it the pre-push check silently does not run, which is
the same failure mode as having no check at all.

## Milestones

| | | |
|---|---|---|
| **M0** | Foundations — solution, CI, ADRs, fixture | ✅ complete |
| **M1** | Ingest — parser, rules R1–R6, schema, idempotency | ✅ complete |
| **M2** | Detection — annotate pass, stops, port calls, seven days | ✅ complete |
| M3 | Postgres behind the same ports, Docker Compose | next |
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

Sixteen decisions are recorded in [`docs/adr/`](docs/adr/), including the ones where the answer was
*no*: why not microservices, why not MongoDB, why not event sourcing, and why the redundant index was
deleted rather than justified.

## Reviewing changes

Most of this code is written by an AI agent, which then judges its own work. `/review-pass` splits
that judgement across three independent read-only lanes -- correctness, data integrity, and craft --
each owning specific blocking classes and each told what is *not* its lane. Only four kinds of
finding block a change; everything else is recorded in
[`docs/review-followups.md`](docs/review-followups.md) and never holds it up.
([ADR-0022](docs/adr/0022-three-lane-review-harness.md))

## Licence

MIT. AIS data © Danish Maritime Authority, free for download.
