# ais-pipeline

Turns the Danish Maritime Authority's open AIS feed into trustworthy per-vessel time series —
with provenance on every row, named quality rules, and ingestion you can run twice — then derives
when each tanker stopped, for how long, and whether it was waiting at anchor or working alongside.

That last distinction is the raw material of a laytime calculation.

> **Status:** in progress. M7 (reconciliation) complete — see [Milestones](#milestones).
> Seven days of AIS in; a Statement of Facts compared against it, and the difference priced.

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

## Two engines, one answer

SQLite and Postgres sit behind the same ports, and the integration suite runs every assertion
against both. Ingesting the same seven days into each produces identical counters, and detection
over them produces a **byte-identical fingerprint across all 809 stops** — waiting and working
totals matching to the cent.

```bash
docker compose up -d
dotnet run --project src/AisPipeline.Cli -- detect \
  --postgres "Host=localhost;Port=55432;Database=ais;Username=ais;Password=ais"
```

| | SQLite | Postgres |
|---|---|---|
| detect over 5.37M fixes | 35 s | **18.5 s** |
| storage | **822 MB** | 1,255 MB |

The container runs the TimescaleDB image, and there is deliberately **no hypertable**. A hypertable
partitions by time; this pipeline's dominant read is per-*vessel* and ordered, already served as an
index scan with a presorted key in 27 kB of sort memory. Partitioning by time would scatter each
vessel's fixes across chunks and make the hot path slower. The extension is there for when it earns
its place — compression, or time-range queries — not because the image offers it.
([ADR-0026](docs/adr/0026-postgres-adapter-and-no-hypertable.md))

## What it is for

```
$ ais reconcile --sof statement-of-facts.json --allowed 48
STINGRAY (mmsi 636025106)
  AIS call : 359  2026-09-03 15:38 -> 2026-09-06 21:18

  Anchored                   SoF 2026-09-03 15:38   AIS 2026-09-03 15:38      0 min   Agrees
  AnchorAweigh               SoF 2026-09-04 22:02   AIS 2026-09-04 22:02      0 min   Agrees
  AllFast                    SoF 2026-09-04 23:29   AIS 2026-09-04 22:47     -3 min   Agrees
  LeftBerth                  SoF 2026-09-06 21:18   AIS 2026-09-06 21:18      0 min   Agrees
  NoticeOfReadinessTendered  SoF 2026-09-03 15:44   AIS —                             AisCannotObserve
  CargoCommenced             SoF 2026-09-05 00:41   AIS —                             AisCannotObserve
  CargoCompleted             SoF 2026-09-06 19:48   AIS —                             AisCannotObserve

  demurrage on the document's timeline : 23,983.43 USD
  demurrage on the AIS timeline        : 27,483.43 USD
  difference                           : -3,500.00 USD
```

A Statement of Facts is what the parties agreed happened. AIS is what the ship's transponder says
happened. **Comparing them is the point**, and the difference here is exactly three hours of shore
stop the document claims and AIS is structurally unable to see — 3h × 28,000/day = 3,500 USD.

Note the `AllFast` line. The document says 23:29; AIS saw the vessel stop at 22:47. That **42-minute
gap is not a discrepancy** — a vessel stops moving well before it is made fast, and two real
Statements of Facts put that lag at 30 and 61 minutes. Comparing the two naively and expecting zero
would flag every honest document ever written.

And the price is **the difference between two laytime calculations, not the delta times the rate**.
The same disagreement about berthing is worth real money when laytime starts on berthing and worth
*exactly nothing* when turn time expires first. ([ADR-0031](docs/adr/0031-reconciling-a-statement-of-facts.md))

## The laytime engine

```
$ ais laytime --mmsi 245313000
mmsi 245313000  port call 126  2026-09-01 05:50 -> 2026-09-06 12:20
  from AIS:  waiting 0.0h   working 126.5h
  NOR:       2026-09-01 05:50  (ASSUMED = arrival; AIS cannot observe a notice)

Laytime commenced 2026-09-01 05:50 (berthed before turn time expired)
  2026-09-01 05:50 -> 2026-09-04 05:50     72.00h  Counted       cargo operations
  2026-09-04 05:50 -> 2026-09-06 12:20     54.51h  OnDemurrage   cargo operations
  allowed      72.00h
  used        126.51h
  ON DEMURRAGE 54.51h at 28,000.00 USD/day = 63,598.56 USD
```

127.6M rows of raw AIS in, a dollar figure out. Every hour in the statement belongs to exactly one
line and says why, and `IsBalanced` asserts the lines decompose back to the elapsed time — the same
accounting identity ingest uses. A total nobody can decompose is a total nobody can dispute, and
disputing it is the point.

**The terms in that example are illustrative, not from any charter party.** 72 hours and
$28,000/day are plausible tanker defaults chosen for the demonstration; the engine is correct for
the terms it is given, and the terms are an input. A real claim uses the real fixture's numbers.

**AIS cannot compute demurrage, and the output says so.** Notice of Readiness is an email; hoses on
and off happen hours after berthing; free pratique is a document. The CLI defaults NOR to arrival
and labels that assumption rather than presenting it as observed. What AIS supplies is the half a
Statement of Facts is least able to prove: where the vessel physically was, and when it stopped
moving. ([ADR-0030](docs/adr/0030-laytime-engine.md))

## Querying it

REST is the operational surface — `/vessels`, `/stops`, `/portcalls`, `/quality`, `/runs`, with a
generated OpenAPI document. GraphQL is the analytical one. They do not duplicate each other.

```graphql
{ vessels(shipType: "Tanker", limit: 50) {
    mmsi name
    portCalls { waitingHours workingHours
      phases { phase { sequence phase } stop { durationHours maxDriftNm statusAgrees } } } } }
```

That returns 50 vessels, 60 port calls and 161 phases from the seven-day database in **3 queries**.
Unbatched it would be `1 + 50 + 60 = 111` round trips. Warm latency ~10 ms.

The tests assert the **query count**, not the timing. A GraphQL response looks identical whether it
took one round trip or a hundred, and a hundred queries against a warm local database is still
milliseconds — so timing cannot tell them apart, and having DataLoader wired up is not evidence it
is used. They also assert the property that actually distinguishes batched from unbatched: query
count does not grow with page size.
([ADR-0027](docs/adr/0027-api-surface-and-n-plus-one.md))

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

```bash
ais ingest data/aisdk-2026-09-05.zip     # two-pass, idempotent
ais detect                               # stops + port calls, recomputed in place
ais quality                              # what each rule did: rejected, and flagged
ais laytime --mmsi 219018271             # a statement for the most recent complete port call
ais reconcile --sof statement.json       # that statement against what AIS observed
ais stops --min-hours 6 --complete-only  # detected stops, longest first
ais portcalls --min-waiting-hours 6      # waiting and working hours per call, with the port
```

`detect` names each port call against a committed extract of the **World Port Index**
([`reference/ports/`](reference/ports/README.md)) — 145 ports over the five countries the feed
actually reaches, because its three busiest stop clusters are Goteborg, Kiel and Rostock and none of
them are Danish. On the seven-day window it names a port for 357 of 362 calls.

What it does **not** do is claim the vessel was *in* that port. A World Port Index record is one
nominal point near the harbour entrance, and measured against those 362 calls the distance to it is
continuous with no gap — vessels plainly alongside sit at 0.11 nm (Arhus) and 3.60 nm (Rostock),
while a mid-Kattegat anchorage sits 14.35 nm off Kalundborg. No radius separates those, so the port
is stored **with its distance** and `ais portcalls` prints both, marking with a `~` the ones too far
out to call the port's own:

```
  id     mmsi       arrived (UTC)      waiting  working  unclassified  nearest port
  126    245313000  2026-09-01 05:50        0.0    126.5           0.0   Marstal 3.1 nm
  350    636016302  2026-09-02 12:47       83.6      0.0           0.0  ~Lysekil 9.2 nm
  359    636025106  2026-09-03 15:38       30.4     46.5           0.0   Fredericia 1.8 nm
```

The `~` rows are the ones with no working hours, which is the point: drift geometry decided
berth-versus-anchorage without knowing about distance, and the two agree — calls within 5 nm are
three and a half times more likely to have berthed (59.7% against 16.8%). The 5 nm figure is a
heuristic and [ADR-0034](docs/adr/0034-ports-are-named-with-a-distance-not-a-boundary.md) says so,
along with why more AIS data will never refine it.

This is also what lets `reconcile` check the port a Statement of Facts names, rather than matching a
document to a call on timestamps alone.

`reconcile` picks the AIS call **by the document's own window** — the call sharing the most time with
it — rather than asking for the vessel's latest and validating that guess
([ADR-0035](docs/adr/0035-select-the-call-the-document-describes.md)). Statements of Facts arrive
weeks after the event, so selecting by recency refused most real documents. An exact tie is refused
rather than broken: two calls sharing the same amount of time cannot be told apart from timestamps,
and picking one would silently decide which timeline a demurrage figure is measured against.

`ais quality` reports **both** things a rule can do, because a rule does exactly one of two things
and there is no third (ADR-0006): it *rejects* a row into `quarantine`, or it *keeps* the row and
flags the doubt. Reporting only the first hides every rule that flags — R7, R8, R11 — and an
invisible doubt reads as no doubt at all (ADR-0032). A registered rule that never fired prints zero
and is named as silent, because silence and absence are different claims:

```
  rule      rejected      flagged  what it catches
  R1               0            0  Row could not be parsed: wrong field count or malformed value
  R4               9            0  Position sentinel, null island, or coordinate out of range
  R5               0           10  Speed over ground unavailable; stored as null and flagged
  ...
  R1, R6, R7, R8, R11 ran and never fired on this data.

  totals: 9 rejected into quarantine, 10 kept with a flag.
```

`ais stops` and `ais portcalls` print a figure the pipeline will not stand behind as a bound, never
as a number: `>=2.7` for a stop whose true extent is unknown because it touches a coverage gap or
the edge of the window (ADR-0011), and `?` for a drift computed from too few surviving fixes to mean
anything (ADR-0025). Both lists are ordered longest first rather than chronologically, and both say
so when the page came back full — a list returned at exactly its limit is otherwise
indistinguishable from a complete one (ADR-0028).

The counts are over what the store holds, not over lines read — `ais ingest` reports the latter,
and it is legitimately the larger number once duplicates in the file collapse onto one natural key.

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
| **M3** | Postgres behind the same ports, Docker Compose | ✅ complete |
| **M4** | REST + OpenAPI, GraphQL with DataLoader | ✅ complete |
| **M6** | Laytime engine — terms in, line-item statement out | ✅ complete |
| **M7** | Statement of Facts reconciliation, priced | ✅ complete |
| M5 | OpenTelemetry → Prometheus + Grafana, k6 | deferred — infrastructure, and nothing queries this yet |

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

Twenty-eight decisions are recorded in [`docs/adr/`](docs/adr/), including the ones where the answer was
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
