# ais-pipeline

**An independent record of when your vessel arrived, when it berthed, and when it left — built from
the public AIS feed, and traceable line by line back to the broadcast it came from.**

A Statement of Facts is what the parties agreed happened. AIS is what the ship's own transponder
said happened, recorded by a device with no stake in the outcome. When a demurrage claim turns on a
contested timeline, that second record is usually the missing evidence — and this turns the Danish
Maritime Authority's open feed into it: per-vessel time series, then stops, then port calls split
into **waiting at anchor** and **working alongside**, which are the two quantities a laytime
calculation is built from.

MIT licensed, runs on your own machine, and it declines to print any figure it will not stand
behind.

## What it costs you not to have this

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

The difference is three hours of shore stop the document claims and AIS is structurally unable to
see — 3 h × 28,000/day = 3,500 USD, on one call.

Note the `AllFast` line, because it is why naive comparison fails. The document says 23:29; AIS saw
the vessel stop at 22:47. That **42-minute gap is not a discrepancy** — a vessel stops moving well
before it is made fast, and two real Statements of Facts put that lag at 30 and 61 minutes. Compare
the two timelines expecting zero and you flag every honest document ever written.

The price is **the difference between two laytime calculations, not the delta times the rate.** The
same disagreement about berthing is worth real money when laytime starts on berthing and worth
exactly nothing when turn time expires first.
([ADR-0031](docs/adr/0031-reconciling-a-statement-of-facts.md))

*The terms above are illustrative. 48 hours and 28,000 USD/day are plausible tanker defaults chosen
for the demonstration; the engine is correct for the terms it is given, and the terms are an input.*

## Try it

```bash
git clone <this repo> && cd ais-pipeline

# One day of AIS from the Danish Maritime Authority, free to download.
# Note the host: web.ais.dk now serves an expired certificate, so the archive is the S3 bucket.
curl -O http://aisdata.ais.dk.s3.eu-central-1.amazonaws.com/aisdk-2026-09-05.zip

./scripts/refresh.sh aisdk-2026-09-05.zip   # ingest, detect, export, build the site
```

One day is ~590–750 MB zipped, ~17M rows, of which ~1.4M are tankers across ~300 vessels. Files are
published with roughly a three-day lag, so there is no live variant of this source. `data/` is
gitignored; if you would rather not download 640 MB, the committed
[`fixtures/sample.csv`](fixtures/sample.csv) holds 744 real rows chosen to exercise every rule
(see [the manifest](fixtures/MANIFEST.md)) and `ais ingest fixtures/sample.csv` works on it.

Then price a call, and ask whether the wait was unusual:

```bash
ais laytime --mmsi 245313000 --allowed 72 --rate 28000
ais portcalls --min-waiting-hours 6
```

Or open the browser page, which does both against your own database:

```bash
cd src/AisPipeline.Api && AIS_SQLITE=../../data/ais.db dotnet run
# then open http://localhost:5000
```

Every verb is in [`docs/cli.md`](docs/cli.md). You need .NET 10; the site additionally needs Node.

## Why you can argue from these numbers

A demurrage figure is only worth as much as its weakest input, so the pipeline is built around one
rule: **a number you cannot trace is a number you cannot trust.**

- **Every stored row points back** to the ingest run, source file and line it came from.
- **Nothing is dropped silently.** A rule either *rejects* a row — writing a quarantine record with
  the rule id and the raw text — or *keeps* it and flags the doubt. There is no third option, and
  `ais quality` prints both columns for every rule, including the ones that never fired.
- **Ingest is idempotent.** The same file twice inserts zero the second time. This matters more than
  it sounds: 38% of a DMA file is duplicate on arrival, because the feed merges receiving stations.
- **Disagreement is recorded, not resolved.** Where a vessel's self-reported status contradicts its
  own measured speed, both readings are stored and the conflict is marked. Nobody picks a winner.
- **Every total decomposes.** Each hour in a laytime statement belongs to exactly one line with a
  reason attached, and `IsBalanced` asserts the lines sum back to the elapsed time. A total nobody
  can decompose is a total nobody can dispute, and disputing it is the point.
- **A figure the pipeline will not stand behind never prints as a number.** `≥42.2` for a duration
  whose true extent is unknown, `?` for a drift from too few surviving fixes, `~Goteborg 7.7 nm` for
  near-but-not-alongside, `open` for a call still in progress.

The reasoning behind each of those is in [`docs/adr/`](docs/adr/README.md) — 42 records, none edited
after acceptance.

## What AIS cannot tell you

| AIS supplies | AIS cannot supply |
|---|---|
| Arrival at the anchorage | Notice of Readiness — an email, not a physical event |
| Berthing, to the minute | Hoses on and off; a tanker lies alongside for hours before pumping |
| Departure | Free pratique, customs clearance |
| Waiting versus working hours | The charter party terms themselves |

**AIS cannot compute demurrage, and the output says so.** The CLI defaults Notice of Readiness to
arrival and *labels that assumption* rather than presenting it as observed, and it refuses outright
to invent a berth time for a call that only ever lay at anchor. What AIS supplies is precisely the
half a Statement of Facts is least able to prove: where the vessel physically was, and when it
stopped moving. ([ADR-0030](docs/adr/0030-laytime-engine.md))

## The rest

| | |
|---|---|
| [`docs/cli.md`](docs/cli.md) | Every verb, its flags, and how to read the output |
| [`docs/the-data.md`](docs/the-data.md) | What was actually wrong with the feed — measured, not assumed |
| [`docs/rules/detection.md`](docs/rules/detection.md) | How a stop and a port call are defined, and the thresholds |
| [`docs/rules/quality-rules.md`](docs/rules/quality-rules.md) | The reject-or-flag contract, and how to add a rule |
| [`docs/rules/units-and-geodesy.md`](docs/rules/units-and-geodesy.md) | Nautical miles, knots, UTC, and the Danish-locale parsing trap |
| [`docs/rules/checks-and-review.md`](docs/rules/checks-and-review.md) | What blocks a merge, and how to run the checks |
| [`docs/adr/`](docs/adr/README.md) | Why the design is what it is |
| [`docs/roadmap.md`](docs/roadmap.md) | Milestones, what is next, and what this does not do |
| [`site/`](site/) | The static site, built from the committed export |

Architecture: a modular monolith, hexagonal, with a core that has no I/O dependencies at all
([ADR-0002](docs/adr/0002-modular-monolith-over-microservices.md),
[ADR-0003](docs/adr/0003-hexagonal-architecture.md)). Two storage engines behind one port — SQLite
and Postgres — running the same test suite, because adapter parity is a claim until both are
measured ([ADR-0026](docs/adr/0026-postgres-adapter-and-no-hypertable.md)).

`./scripts/check.sh` runs format, build and all three test projects. It runs pre-push and in CI.

## Licence

MIT — see [LICENSE](LICENSE).
