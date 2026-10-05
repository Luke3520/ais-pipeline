# ais-pipeline

**A tanker radar for your terminal, on a pipeline that refuses to make anything up.**

![ais radar replaying a night in the Kattegat, with the ship's log typing underneath](docs/demo/radar.gif)

That's a week of the Danish Maritime Authority's open AIS feed (5.37 million position reports from
452 tankers) replayed on a green screen. Arrows are ships under way, pointing along the course they
reported. `o` is a ship stopped. An amber **`!`** is a ship contradicted by its own transponder,
usually sitting at anchor while broadcasting *under way using engine*. In one measured day,
[63% of tanker fixes below half a knot](docs/the-data.md) were claiming exactly that.

Every line of the ship's log ends in a citation like `[r4·L16609354]`: ingest run 4, line
16,609,354 of the file that run read. One query takes you back to the broadcast:

```sql
SELECT * FROM position_report WHERE ingest_run_id = 4 AND source_line = 16609354;
```

## Thirty seconds, no download

The repository carries 744 real rows chosen to exercise every rule ([`fixtures/`](fixtures/MANIFEST.md)).
You need .NET 10.

```bash
git clone https://github.com/Luke3520/ais-pipeline && cd ais-pipeline
alias ais='dotnet run --project src/AisPipeline.Cli --'

ais ingest fixtures/sample.csv --ship-type Tanker   # parse, check, store, with provenance
ais detect                                          # stops, port calls, contradictions
ais radar                                           # q to quit, space to pause, +/- for speed
```

The fixture is two and a half hours of one morning, so the scope is sparse. For the screen above,
[download a day or a week](#try-it) and point the radar at `--region kattegat`. The findings are
also [a website](https://luke3520.github.io/ais-pipeline/), built from the same data. `ais log --mmsi <n>`
prints one vessel's whole log, and `ais radar --at <instant>` prints a single frame and exits.

## The AI writes the log. A program checks every number.

`ais log --mmsi 636025106 --narrate` hands the cited log to Claude and asks for it back in the
voice of the ship's master. A fluent model will happily round *30.4 h* to *some thirty hours*,
add two durations together, or produce a plausible time nobody recorded. All of those read well,
and all of them are numbers you can't trace.

So nothing the model writes is printed until [`CitedNarrative`](src/AisPipeline.Core/Narration/CitedNarrative.cs)
has checked it. **Every sentence must cite a log line it was given, and every number in a sentence
must appear in the lines that sentence cites.** Truncating 22:02:21 to 22:02 passes. Rounding,
arithmetic across entries, and an invented hour don't. One failing sentence refuses the whole
narration, which is never trimmed to its good half. The reader gets the reasons and then the plain
log. A refusal looks like this (an illustration; the format is the program's):

```
NARRATION REFUSED: 1 problem(s) in what claude-opus-5-5 wrote.
  - sentence 2 says 30, which none of its citations contain
The plain log follows.
```

The validator can't catch a false claim made of words alone, and the output says so.
[ADR-0050](docs/adr/0050-narration-is-cited-or-it-is-refused.md) has the reasoning, including what
leaves the machine: one vessel's name and its log lines, nothing else.

## Old school, new school

| | |
|---|---|
| **Braille graphics** | Every dot on the scope is one of the eight in a Unicode braille cell. That's eight times the resolution of text, in any terminal |
| **P1 phosphor** | The green radar screens and the VT100 shipped with. `--amber` gives you P3, which the people who stared at them all day asked for |
| **A teletype log** | The newest line types itself out. If it falls behind, it prints at once, because a log that lags the scope lies about what just happened |
| **An LLM on a leash** | It may write the log, but a regex decides whether anyone reads it |
| **Decision records** | [Every decision](docs/adr/README.md), with the measurement it rests on. The radar has [one too](docs/adr/0049-the-radar-is-a-view-not-a-feature.md) |

The radar decides nothing for itself. *Stopped*, *contradicting itself* and *on its Fredericia call*
are all read from what detection and the quality rules already stored. A radar with its own opinion
would be a second opinion that could disagree with the first.

## What it's actually for

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

### What it costs you not to have this

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
git clone https://github.com/Luke3520/ais-pipeline && cd ais-pipeline

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
# opens http://localhost:5273
```

Pick a call, put your charter party terms in, and read the statement line by line: every hour
between commencement and completion sits on exactly one line with a reason attached. Calls it
cannot price say so, and say why. `Port waits` ranks a wait against what other vessels actually
experienced at that port.

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

The reasoning behind each of those is in [`docs/adr/`](docs/adr/README.md), where a record is never
edited after acceptance — a reversed decision gets a new one that supersedes it.

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
| [`site/`](site/) | The static site, built from the committed export: [luke3520.github.io/ais-pipeline](https://luke3520.github.io/ais-pipeline/) |

Architecture: a modular monolith, hexagonal, with a core that has no I/O dependencies at all
([ADR-0002](docs/adr/0002-modular-monolith-over-microservices.md),
[ADR-0003](docs/adr/0003-hexagonal-architecture.md)). Two storage engines behind one port — SQLite
and Postgres — running the same test suite, because adapter parity is a claim until both are
measured ([ADR-0026](docs/adr/0026-postgres-adapter-and-no-hypertable.md)).

`./scripts/check.sh` runs format, build and all three test projects. It runs pre-push and in CI.

## Licence

MIT — see [LICENSE](LICENSE).
