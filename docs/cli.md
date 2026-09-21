# The `ais` command

The ten verbs, what they print, and the grammar they print it in. The source of truth is `Usage()`
in [`src/AisPipeline.Cli/Program.cs`](../src/AisPipeline.Cli/Program.cs) — if this file and the
program disagree, the program is right and this file is a bug.

```bash
ais ingest data/aisdk-2026-09-05.zip     # two-pass, idempotent
ais detect                               # stops + port calls, recomputed in place
ais quality                              # what each rule did: rejected, and flagged
ais laytime --mmsi 219018271             # a statement for the most recent complete port call
ais reconcile --sof statement.json       # that statement against what AIS observed
ais stops --min-hours 6 --complete-only  # detected stops, longest first
ais portcalls --min-waiting-hours 6      # waiting and working hours per call, with the port
ais export --out site/src/data           # the static site's data contract
ais prune --keep-days 90 --force         # archive old port calls, then drop what is past the bar
ais archive archive/port-calls-*.json    # read back what prune wrote
```

`detect` names each port call against a committed extract of the **World Port Index**
([`reference/ports/`](../reference/ports/README.md)) — 145 ports over the five countries the feed
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
heuristic and [ADR-0034](adr/0034-ports-are-named-with-a-distance-not-a-boundary.md) says so,
along with why more AIS data will never refine it.

This is also what lets `reconcile` check the port a Statement of Facts names, rather than matching a
document to a call on timestamps alone.

`reconcile` picks the AIS call **by the document's own window** — the call sharing the most time with
it — rather than asking for the vessel's latest and validating that guess
([ADR-0035](adr/0035-select-the-call-the-document-describes.md)). Statements of Facts arrive
weeks after the event, so selecting by recency refused most real documents. An exact tie is refused
rather than broken: two calls sharing the same amount of time cannot be told apart from timestamps,
and picking one would silently decide which timeline a demurrage figure is measured against.

`ais quality` reports **both** things a rule can do, because a rule does exactly one of two things
and there is no third (ADR-0006): it *rejects* a row into `quarantine`, or it *keeps* the row and
flags the doubt. Reporting only the first hides every rule that flags — R7, R8,
R11, R12, R13 — and an invisible doubt reads as no doubt at all (ADR-0032). A registered rule that never fired prints zero
and is named as silent, because silence and absence are different claims:

```
  rule      rejected      flagged  what it catches
  R1               0            0  Row could not be parsed: wrong field count or malformed value
  R4             165            0  Position sentinel, null island, or coordinate out of range
  R5               0          315  Speed over ground unavailable; stored as null and flagged
  R6              65           22  Speed over ground implausible for a tanker
  R7               0          776  Implied speed over 50 kn across more than 0.5 nm
  R8               0        1,348  No fix for more than 60 minutes
  R11              0        2,869  Reported speed contradicted by movement over more than 0.1 nm
  R12              0        4,055  Reported status claims stationary while speed says under way
  R13              0      353,069  ETA more than 60 days ahead; a stale one the decoder rolled over

  R1 ran and never fired on this data.

  totals: 230 rejected into quarantine, 362,454 kept with a flag.
```

**R13 is 97% of that flag total on its own** — 353,069 rows of 5.37M, one in fifteen. It only
became visible once the voyage fields were stored at all (ADR-0040), and what it catches is not a
corrupt number but a human one: ETA is typed in by the crew, and a value left from a previous
voyage rolls over into a date months ahead rather than expiring. The threshold of 60 days sits in a
run of days where no fix lands at all, measured at 42-76 across the seven days
([ADR-0041](adr/0041-r13-a-stale-eta-goes-forward-not-backward.md)), so its exact value cannot
change the classification. Nothing is rejected: a stale ETA is still what the vessel broadcast.

R12 is the mirror of R10 — a status claiming stationary while the speed says under way, where R10
is a status claiming under way while the vessel sits still
([ADR-0038](adr/0038-r12-the-mirror-of-r10.md)). The two together are why the dial on the
bridge, not the receiver, is the least reliable part of this feed.

That table earned its keep the first time it ran. R6 was documented for months as a guard that had
never fired — true of the 1.7M-row sample it was written against, and false of the seven-day window,
where it rejects 65 rows and flags 22. Nothing could have noticed until there was a verb that printed
the counter.

`ais stops` and `ais portcalls` print a figure the pipeline will not stand behind as a bound, never
as a number: `>=2.7` for a stop whose true extent is unknown because it touches a coverage gap or
the edge of the window (ADR-0011), and `?` for a drift computed from too few surviving fixes to mean
anything (ADR-0025). Both lists are ordered longest first rather than chronologically, and both say
so when the page came back full — a list returned at exactly its limit is otherwise
indistinguishable from a complete one (ADR-0028).

R10 — a vessel's own navigational status contradicting its own speed — is reported below that table
rather than in it, because it counts **stops** and the table counts **rows**. Placing 299 beside 9
would invite a comparison the two numbers do not support
([ADR-0036](adr/0036-r10-is-reported-separately-because-it-counts-something-else.md)). It is
also the one rule that implements rule 4 rather than rule 2: nothing is rejected and nothing is
flagged, both readings are stored, and neither wins.

The counts are over what the store holds, not over lines read — `ais ingest` reports the latter,
and it is legitimately the larger number once duplicates in the file collapse onto one natural key.
