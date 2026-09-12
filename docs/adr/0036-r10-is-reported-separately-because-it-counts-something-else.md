# 36. R10 is reported separately, because it counts something else

Date: 2026-09-12

## Status

Accepted

## Context

CLAUDE.md's definition of done says a quality rule's counter must be visible in `ais quality`. R10 —
a vessel's navigational status contradicting its own speed — was visible nowhere in it.

It is not an oversight in the report. R10 is unlike every other rule here:

- It implements **rule 4** (record disagreement, do not resolve it), not rule 2 (reject or flag).
- It judges a **stop**, not a row, so it has nothing to write to `position_report.quality_flags`.
- It is stored as `stop_event.status_agrees`, a column on a derived record.

So the report — built from `quarantine` and `quality_flags` — structurally cannot see it. Meanwhile
`RuleIds` claimed every id is persisted "in `quarantine.rule_id` and in
`position_report.quality_flags`", which was simply untrue of R10.

The obvious fix is a row in the rules table. That would be a wrong number under a correct heading:
the `rejected` and `flagged` columns count rows out of ~5.4 million, and R10 counts stops out of 809.
Placing 299 beside 9 invites the reader to compare them, and the two are not comparable.

## Decision

**R10 gets its own line in `ais quality`, below the table, labelled with what it counts.**
`StatusDisagreement()` returns the disagreeing count, the total, and the share, and the output says
the count is over stops rather than rows and where the flag lives.

**It is not a `RuleHitCount`.** A separate read model, because a shared shape would eventually put it
in a shared column — and the rule it would break is the one this project exists to enforce: a number
you cannot compare is a number you should not print beside one you can.

**Both counts come from one scan.** Asking for the total and the disagreeing count separately could
report a share computed across two states of the table if `detect` ran in between.

**`RuleIds`' claim about persistence is corrected**, and R10's constant now documents where it
actually lives.

## Consequences

Every documented rule now has a counter in `ais quality`, which is what the definition of done asks
for. On the seven-day window: 299 of 809 stops, 37.0%.

`detect` prints the same figure at the end of a run. That is duplication, and deliberate: `detect`
reports what it just computed, `quality` reports what the store holds, and those differ the moment
anyone ingests more data without re-running detection. A single figure printed in one place only
would be the more elegant and the less useful arrangement.

The report now draws from three sources — `quarantine`, `quality_flags`, `stop_event` — and a fourth
kind of rule would need a fourth. That is a real cost, accepted because the alternative flattens
distinctions the pipeline exists to preserve. If a fifth arrives, the right move is probably to give
every rule a declared unit rather than to keep adding sections.
