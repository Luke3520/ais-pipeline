# 31. Reconciling a Statement of Facts against AIS

Date: 2026-09-11

## Status

Accepted

## Context

A Statement of Facts is the chronological record of a vessel's port call — arrival, berthing, cargo
operations, delays, departure — prepared by the agent or master and signed by the master, the agent
and a charterer's representative. It is the document a demurrage claim is argued from.

It is also one side of a story. The pipeline now has the other: an independent record of where the
vessel physically was and when it stopped moving, from a transponder with no stake in the outcome.

Three real documents shaped this milestone: BIMCO's Standard Statement of Facts (Oil and Chemical
Tank Vessels), a Peruvian agent's form from Chimbote in 2018, and a Humber agent's from Immingham in
2023.

## Decision

**A Statement of Facts is modelled as a chronological event list, not as a fixed-field form.**

The three documents share no layout. BIMCO has forty numbered boxes; Chimbote has a fixed arrival
block plus a free table; Immingham has thirty-six free-text rows. A model shaped like any one of
them fits neither of the others. All three reduce to `(timestamp, label, remark)`, with the label
*classified* rather than replaced — the agent's own words are kept, always, because the wording is
what a dispute turns on.

Classification matches **conjunctions of stems in any order**. Agents do not write phrases
consistently: "Completed discharging" and "Discharging completed" are one event with the words
swapped, and the Chimbote document reads **"Commence Dischargie"** — a typo that is still an
unambiguous cargo start. Phrase matching catches only documents nobody fat-fingered. Unrecognised
labels become `Other` and are retained; a classifier that dropped what it could not name would
silently shrink the document.

**The comparison uses expected offsets, not just tolerances.**

This is the milestone's central finding. A vessel stops moving well before it is made fast, so
comparing "all fast" against the AIS stop and expecting zero would flag every honest document ever
written. The two documents bracket the sequence themselves:

| | first line ashore → all fast |
|---|---|
| Immingham, Aug 2023 | **30 min** (tug made fast → all fast: 60 min) |
| Chimbote, May 2018 | **61 min** |

So the expected lag is roughly 30–60 minutes, and only what remains after subtracting it is a
discrepancy. This matters commercially rather than cosmetically: many charter parties start laytime
only once the vessel is all fast, so at 28,000 USD/day an hour of error here is several hundred
dollars on one line item.

**A discrepancy is priced as the difference between two laytime statements, never as the delta
times the rate.**

The intuitive approach — 27 minutes disputed, therefore 27 minutes of demurrage — is wrong, and
wrong in both directions. What a discrepancy costs is a property of the charter party, not of the
discrepancy. The same four-hour disagreement about berthing is worth real money when laytime
commences on berthing, and **exactly nothing** when turn time expires first and both timelines
start at the same moment. It can also be worth far more than its own duration if it pushes the
total across the allowance. So: run the M6 engine twice, once on each timeline, and diff.

**Disagreement is recorded, not resolved.** Neither source is declared correct — the same position
rule R10 takes when a vessel's navigational status contradicts its own speed. Here one account is
signed by three parties and the other is a transponder, and the honest output is both.

**Events AIS cannot observe are listed rather than omitted**, so silence does not read as agreement.
Notice of Readiness is an email; hoses, arms and pumps are not visible from space.

## Amendments from review

Four things the three-lane review surfaced, all fixed before this record settled.

**"Vessel unmoored" classified as arrival.** `"moored"` is a substring of `"unmoored"`, and the
`AllFast` rule was tested before `LeftBerth`. A document using that vocabulary lost its departure
time from both the comparison and the priced statement, and laytime ended at cargo completion
instead. Departure is now tested first.

**The printed audit trail did not match the printed price.** The comparison table showed
`First(LeftBerth)` while the statement was priced from `Last(LeftBerth)` — and the classifier folds
"last line" and "vessel sailed" into one kind, so a document recording both showed one time and
charged another. Both now use the earliest departure marker, which is also the right one: it is
when the vessel starts moving, which is what AIS corresponds to.

**Recognised duplicates vanished.** Unrecognised labels were reported as "kept but not classified";
recognised duplicates of a compared kind were not reported at all. The real Immingham document has
four "NOR re-tendered" lines — three of them appeared nowhere. Every event is now accounted for as
compared, uncorroborated, unclassified, or explicitly listed as not used.

**The inferred offset was invisible where it mattered.** An "Agrees" that already had 45 unmeasured
minutes subtracted from it is a different claim from one that needed none, and the caveat lived only
in this ADR. The offset is now named on the line that prints the verdict, and a document's
`preparedBy` — which reads "constructed fixture" for the committed sample — prints beside the figure
derived from it.

## Consequences

- **The all-fast lag is inferred, not measured.** It comes from the internal structure of two
  documents, not from comparing either against AIS — both calls (Immingham, −0.19°E; Chimbote,
  Peru) fall outside DMA's coverage, measured from the data as lon 4.6–15.8°E, lat 54.2–58.2°N.
  The experiment that would settle it: a Statement of Facts for a call at Gothenburg, Fredericia,
  Aarhus, Copenhagen, Malmö, Kiel or Rostock, whose day can then be ingested and measured directly.
  This is the same status ADR-0020 held before seven days of data moved it by a factor of thirty.
- **The tolerance for the other physical events is uncalibrated** and says so. No available document
  measures dropping anchor, weighing it, or last line off against AIS.
- **No architecture trigger was crossed.** ADR-0029 names a write surface and commercially sensitive
  data as conditions for re-opening the design. A Statement of Facts arrives as a structured local
  file read by the CLI, exactly as AIS archives do — no upload endpoint, no authentication, no
  tenancy. Those triggers arrive when someone other than the operator needs to put a document in.
- **Document extraction is deliberately out of scope.** The sample forms are scans from an office
  photocopier — one carries two characters of text layer and page images instead — so ingesting a
  real SoF is an OCR problem. OCR output must never move a demurrage figure without a human in
  between, and fencing that stage off keeps a bad read at the boundary rather than inside a claim.
