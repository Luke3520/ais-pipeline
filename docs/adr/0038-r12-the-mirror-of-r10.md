# 38. R12 — a stationary claim contradicted by the vessel's own speed

Date: 2026-09-12

## Status

Accepted

## Context

R10 records a vessel whose navigational status says "under way" while its speed and drift say it is
stopped: a dial left uncorrected **on arrival**. It fires on 297 of 809 stops — 37% — and those
vessels moved a mean of 92 metres over a mean of 5.7 hours.

The mirror case had no rule. A vessel that leaves a berth and keeps broadcasting "Moored" — a dial
left uncorrected **on departure** — was invisible, and R10 could never see it: R10 is computed
inside stop detection, so it only ever asks its question of a vessel that has stopped. Nothing in
`Core/Quality` examined navigational status at all.

It is not hypothetical. Across the seven-day window, **4,122 fixes from 41 vessels** report "Moored"
or "At anchor" while making more than 3 knots, at a mean of 7.8 kn and a maximum of 15.5 kn — a ship
at service speed telling the world it is tied up.

The asymmetry mattered beyond the missing rows. The pipeline could catch a vessel misreporting in
one direction and not the other, while presenting `ais quality` as a complete account of what the
rules found.

## Decision

**R12 flags a row whose status claims stationary while its speed says otherwise.** A record rule,
like R4 and R6: it judges one row against itself and needs no ordering, so it runs during ingest
rather than in the annotate pass.

**It flags and never rejects.** The position and speed on such a row come from the receiver and are
sound; only the hand-typed status is doubtful. Rejecting would discard good track to punish a
forgotten dial, and would lose the very rows the finding is made of.

**The threshold is 3.0 knots, taken from a trough rather than asserted.** Speed over ground across
the 227,258 fixes claiming a stationary status is bimodal:

| SOG | fixes |
|---|---|
| 0.0 | 157,515 |
| 0.0–0.5 | 60,618 |
| 0.5–1 | 3,908 |
| 1–2 | 748 |
| **2–3** | **326** |
| 3–5 | 1,267 |
| 5–8 | 1,047 |
| 8+ | 1,808 |

The count decays monotonically to a minimum at 2–3 kn and then rises again. Two populations: a
moored vessel working against tide and GPS noise, and a ship under way. The minimum between them is
the boundary, and the boundary is exclusive — exactly 3.0 does not fire.

This is a measured threshold, which ADR-0020's berth distance was not; that one was guessed and
later moved by a factor of thirty. It remains a heuristic, and a fix either side of it is a question.

**A status that claims nothing cannot be contradicted.** "Not under command" means unable to
manoeuvre and says nothing about making way — a broken-down ship still drifts, often fast — so it
classifies as `Unknown` and R12 ignores it, as does R10. The same for DMA's "Unknown value"
placeholder.

## Consequences

`ais quality` reports R12 with no further wiring, because the report is built from the registry
(ADR-0032). The two halves of the same human failure now appear together, one counted over rows and
one over stops (ADR-0036).

**The counts change on re-ingest, not retroactively.** R12 runs during ingest, so a store built
before this record has no R12 flags and its `quality_flags` are silently incomplete rather than
zero. Unlike the detection projections, `position_report` is the immutable log and is not rebuilt —
so the honest fix is a fresh ingest, and the alternative, an annotate-style backfill pass, is a
larger change than the rule itself.

R10 and R12 are deliberately not merged into one "status disagreement" rule. They are measured
against different things — a stop's drift and duration, versus a single row's speed — they live in
different tables, and they count different units. One id per question keeps both answerable.
