---
paths: src/AisPipeline.Core/Quality/**, tests/AisPipeline.Tests/Quality/**
---

# Quality rules

A rule is the unit of trust in this pipeline. Every one has an id, a pure function, unit tests, a
declared outcome, and a visible counter.

## The contract

Each rule implements `IQualityRule` and declares exactly one outcome:

- **Reject** — the row does not enter `position_report`. A `quarantine` row is written with the
  rule id, source file, source line, raw text and a detail message. The evidence is kept so a rule
  fixed later can be re-applied without a full re-ingest.
- **Flag** — the row enters `position_report` with the rule id appended to `quality_flags`. The
  observation is kept and the doubt is kept with it.

There is no third outcome. A row that leaves the pipeline without a quarantine record, a flag, or a
counter is a silent loss — blocking class 1.

## Adding or changing a rule

1. Give it the next free id. **Ids are never reused**, including for rules that were dropped.
   R2's id stays R2 even though duplicate handling moved into a database constraint.
2. Write it as a pure function in `Core/Quality` — no I/O, no database, no clock.
3. Unit-test it against the boundary values of every comparison it makes, not just its branches.
4. Register it in the rule registry so `ais quality` reports it without a code change.
5. State its expected hit rate, measured on real data, in the rule table in the README. A rule that
   has never fired on real data is worth keeping only as a stated guard — say which it is.
   **A dormancy claim must name the window it was measured on.** "R6 has never fired" was true of
   the 1.7M-row sample it came from and false of the seven-day window, where it rejects 65 rows and
   flags 22 — and it had spread to the rule's own docstring and two ADRs before `ais quality`
   existed to print the counter. Write "has not fired in the seven days to 2026-09-07", which ages
   into a checkable statement instead of a wrong one.
6. If it changes what a stored row looks like, it needs an ADR.

## Measure before you write

Rules here are written against measured reality, not against what AIS documentation implies. Six of
the eight originally planned rules fired on essentially nothing, because the DMA feed blanks its
sentinel values rather than emitting them: no `102.3` speed, no `511` heading, and the position
sentinel is latitude `91` with longitude `0`, never `181`.

Before adding a rule, run it over a real file and record the hit rate. A rule justified by a
plausible story and no measurement is how the previous rule set got written.

## Row-local versus sequence rules

**Row-local** rules (malformed row, sentinel position, unavailable speed, implausible speed) run
during ingest, in either pass, and see one row.

**Sequence** rules (teleport, coverage gap, speed/position consistency) need a vessel's fixes in
time order. They run as a **post-ingest annotate pass reading from the store ordered by
`(mmsi, ts_utc)`** — never from the file stream, which is what makes them correct across day
boundaries.

The annotate pass completes **before** detection, and detection excludes flagged fixes from
centroid and drift computation. One corrupt position otherwise drags the centroid *and* supplies
the maximum, producing a 2,387 nm drift over hundreds of good fixes (ADR-0021).
