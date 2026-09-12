# 39. An export contract, so the site never touches the database

Date: 2026-09-13

## Status

Accepted

## Context

ADR-0037 put a read-only page inside the API and kept it same-origin. The next site is a different
thing: public, content-heavy, and built in Astro. Two ways to feed it —

- **The browser talks to the API.** Means hosting the pipeline publicly: a .NET app and an 822 MB
  database exposed to the internet, with caching, rate limiting and an uptime expectation attached
  to a hobby project.
- **The site is built from a file.** The pipeline emits JSON, the site compiles it in at build time,
  and what ships is static.

The second is not a compromise here, it is the honest shape. **This source has no live variant.**
The DMA archive publishes with a measured three-day lag (ADR-0029), so a request-time query answers
with data that was already three days old when it was ingested. Paying for a live read path to serve
batch data is paying for a property the data does not have.

## Decision

**`ais export --out <dir>` writes one document, and that document is the contract.** The site depends
on the file, never on the schema. Columns can be renamed, the store can move from SQLite to Postgres,
and the site keeps building.

**The document carries its own provenance.** Which source files, which window, how many rows read
and stored, and when it was generated. Rule 1 says a number you cannot trace is a number you cannot
trust; that does not stop being true because the number is on a web page instead of in a table. A
site built from this can state where its figures came from rather than asking to be believed.

**Every count ships with its denominator.** A leaderboard of raw counts ranks busy vessels highest,
which measures how often a ship calls rather than how often its crew forgets. The numbers say this
plainly: ranked by count the worst vessel has 26 lapses; ranked by rate, six vessels lapsed on
**every stop they made** — 26 of 26, 21 of 21, 15 of 15. That second list is the finding, and it is
unreachable without the denominator.

**Vessels that never lapsed are counted but not published.** They are needed for the other half of
the distribution — 32 vessels never got it wrong across 173 stops — and emitting all ten thousand
would be most of the file. The summary carries the count; the document carries only the offenders.

**A habit needs at least three stops.** A vessel with one stop is at 0% or 100% and neither means
anything. The claim being made is that a crew behaves the same way every time, which needs
repetitions before it is a claim at all. Three is stated, not tuned.

**Assembled in Core, serialised by the adapter.** The shape a published site depends on is decided
and unit-tested in `ExportBuilder`; the CLI fetches and writes bytes.

## Consequences

The site can be hosted anywhere that serves files, with no database, no API, no CORS and no
authentication — and the pipeline never faces the internet. Rebuilding after an ingest is one
command and a redeploy.

The export is a snapshot, and a stale one is indistinguishable from a fresh one unless someone reads
the manifest. That is why the manifest is not optional and why the site is expected to print the
window it covers.

**The export is committed.** 44 KB of derived JSON in a repository whose rule is that derived data
is rebuilt rather than stored — but the alternative is worse. A site that generates its own data at
build time needs the 822 MB database wherever it builds, which means CI and every contributor. The
committed file makes the site buildable from a fresh clone with nothing but the repository, and it
is reproducible from the pipeline in one command whenever it goes stale.

Finding this also caught a recurrence of a defect `.gitignore` already documents. The rule was
`data/`, unanchored, which matches a directory of that name at any depth — so `site/src/data` was
silently ignored and the export would have been missing from a fresh clone. The comment above that
rule describes the same failure happening once before with `*.csv`. It is now `/data/`.

**A dynamic-typing trap surfaced while building this, and is worth recording.** SQLite types values
per row, so `SUM(CASE WHEN ... THEN hours ELSE 0 END)` returns INTEGER for a vessel that never
lapsed and REAL for one that did. Dapper compiles its deserializer from the first row and then
throws on the first row of the other type — so the query worked or failed depending on which vessel
sorted first. The literal is now `0.0`. Any future aggregate over a nullable or conditional numeric
column needs the same care; the parity suite covers this one.
