# 49. The radar is a view, not a feature

Date: 2026-10-06

## Status

Accepted

Checked against ADR-0029's triggers before any code was written. It crosses none of them, but it
does diverge from the access pattern ADR-0013 said would re-open the index decision. That is
addressed below with a measurement rather than an index.

## Context

The project's findings are hard to see. The README opens on a demurrage reconciliation, which is
the commercial point but means nothing to most people who land on the repository. Yet the most
striking thing in the data is visual and needs no shipping knowledge: tankers sitting still at
anchor while their own transponders insist they are under way (R10), and the mirror image (R12).

`ais radar` draws that in a terminal: a braille coastline, range rings, a sweeping beam, every
tanker as a blip with an hour of trail, and a ship's log typing out what happens, each line ending
with the ingest run and source line it rests on. `ais log --mmsi` prints the same log for one
vessel across the whole window.

Three questions had to be answered before building it.

**1. Is this ADR-0029's map trigger?** That trigger is *a browser UI*: static hosting, CORS, and a
client that wants positions at map scale over a wire, where 5.4M fixes cannot travel as JSON. The
radar is a local process reading the local store through the existing query port. Nothing is
served and nothing crosses a network. It is a map, but not that map, and the trigger's reasoning
(the wire, tiling, Playwright) does not apply. A browser map would still cross the trigger exactly
as ADR-0029 describes.

**2. Does it make any judgments?** It must not. A radar that decided for itself whether a vessel
was stopped, at 0.1 kn with its status on "under way", would be a second opinion on the data that
could disagree with the first. Rule 4 forbids the pipeline from resolving a disagreement, and it
certainly should not invent one.

**3. Does its read path need an index?** Every other reader walks `position_report` in
`(mmsi, ts_utc)` order, which the unique key serves by prefix (ADR-0013). The radar reads **across
vessels in time order**, which no index serves. ADR-0013 said a divergent access pattern re-opens
the decision.

## Decision

**The radar and the log are views. They read, they write nothing, and they decide nothing.**

- *Stopped* means the fix lies inside a stop detection wrote. *Contradicting itself* means R10 on
  that stop or R12 on that fix. A fix that looks like a contradiction but sits in no stop is drawn
  as moving. A test pins that case.
- Fixes a rule found positionally unreliable (R7, R11, `RuleIds.PositionUnreliable`) are neither
  drawn nor followed by the trail, for the reason ADR-0021 excludes them from a centroid.
- A port is named on a log line only for a call plausibly at it (ADR-0034), and as the *call's*
  port ("stopped on its Fredericia call"), never the stop's. The attribution is made from the
  call's centroid, and a call's first stop is often an anchorage miles outside.
- Every log line cites the fix it was emitted on as `[rN·LM]`, prints its time to the second, and
  prints a stop of unknown extent as `≥`. The log is held to the same rules as any other output.

**No index on `ts_utc`.** Time-ordered reads are served by a scan and a sort, streamed through a
`Track` query that Dapper enumerates unbuffered, so the replay holds an hour of trail per vessel
rather than the window.

Measured on the seven-day store (5,368,195 fixes, 1.09 GB SQLite file, Apple Silicon laptop, warm
page cache), wall time from launch to the first drawn frame:

| Window | First frame |
|---|---|
| 1 day (`--hours 24`) | 3.9 s |
| 7 days (`--hours 168`) | 5.1 s |

`sqlite3` alone takes 4.8–5.1 s to produce the first time-ordered row of *any* window: one hour, one
day or seven. That is the full-table scan, the same cost whatever the window. The sort adds about a
second for a week. The replay then runs for minutes, so a five-second wait at launch is paid once.
The screen says "warming up the magnetron" while it happens, which is both a joke and accurate:
old radar sets needed a warm-up too.

## Consequences

- An index on `ts_utc` would cut the first frame to well under a second. It would also add a
  second index write to every one of ~6M inserts on the ingest hot path, which is the cost
  ADR-0013 removed. That trade is wrong for a view. **Revisit if** the radar becomes something
  people wait on repeatedly, or if the store passes the size where a scan costs tens of seconds.
  That condition is also ADR-0029's scale trigger, so the two will be reconsidered together.
- `IAisQueries` gains four methods: `Track`, `StopsOverlapping`, a window-only
  `PortCallsOverlapping`, and `FeedWindow`. `FeedWindow` reads the vessel register (0.02 s) rather
  than `MIN(ts_utc)` over `position_report` (7 s). It is a default for where a replay begins, not a
  figure. All four run in the parity suite on both engines, including the second-wide windows that
  catch the SQLite timestamp-parameter skew.
- The coastline is Natural Earth 1:10m, public domain, clipped by a committed script
  (`reference/coastline/README.md`). It is scenery, and nothing derived reads it.
- The one-vessel `Track` is spelled without `@mmsi IS NULL OR`, so SQLite walks the unique index by
  prefix instead of scanning. `ais log` for one vessel over the whole week returns in about 2 s.
- The radar has no automated test of the terminal itself. Core (projection, braille, replay,
  composite frame) is unit-tested, including a golden frame. The terminal layer was driven in a
  pseudo-terminal at 12×40 and 60×220 and exited cleanly on `q` and Ctrl-C with the screen
  restored. That was a check, not a suite, and a regression there would surface as a person
  noticing.
