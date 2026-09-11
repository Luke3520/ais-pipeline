# 30. A laytime engine, and the boundary of what AIS can prove

Date: 2026-09-11

## Status

Accepted

## Context

Five milestones produced a pipeline: ingest, detection, a second database, an API. None of them
produced a number a chartering desk would act on. Port calls report "30.4 hours waiting, 46.5 hours
working", which is the raw material of a laytime calculation and not a laytime calculation.

## Decision

**Structured charter party terms in, an event timeline in, a line-item statement out.**

Pure interval arithmetic in `Core/Laytime`: no AIS dependency, no database, no clock. The most
testable component in the project, and it has to be — this produces the figure a demurrage claim is
argued over.

**Terms arrive structured, never parsed from prose.** Extracting terms from charter party text is
an LLM problem wearing a rules-engine costume, and it is where this kind of project goes to die.
Unsupported clause types are refused loudly rather than guessed at.

**Money is integer minor units, never a double.** A rate of $28,000/day accrued over hundreds of
hours and rounded at each step drifts by amounts a counterparty notices, and binary floating point
cannot represent most decimal amounts exactly. Demurrage is one rational multiplication — the daily
rate prorated over hours — rounded exactly once, half away from zero, which is the commercial
convention rather than banker's rounding.

**Every hour belongs to exactly one line.** `LaytimeStatement.IsBalanced` asserts the lines
decompose back to the elapsed time, the same accounting identity `IngestCounters.IsBalanced` gives
ingest. A total nobody can decompose is a total nobody can dispute, and disputing it is the point.

**"Once on demurrage, always on demurrage" is implemented, and is a term rather than a constant.**
Once the free time is used, an interruption the charterer would otherwise have been entitled to no
longer stops the clock. Some charters contract out of it, so it is a flag on the terms.

An interval that starts inside laytime and ends outside it is **split at the boundary**. Rounding
it to one side or the other would be wrong by the remainder.

## The boundary, stated plainly

AIS cannot compute demurrage, and the output says so rather than implying otherwise.

| AIS supplies | AIS cannot supply |
|---|---|
| Arrival at the anchorage | Notice of Readiness — an email, not a physical event |
| Berthing, to the minute | Hoses on and off; a tanker lies alongside for hours before pumping |
| Departure | Free pratique, customs clearance |
| Waiting versus working hours | The charter party terms themselves |

The CLI defaults NOR to arrival and **labels that assumption in its output** rather than presenting
it as observed. `VoyageTimeline.FromPortCall` returns null for a call with no berth phase: a vessel
that only lay at anchor has no cargo operations, and inventing a berth time from an anchorage would
put a fabricated timestamp into a commercial claim.

What AIS does supply is precisely the half a Statement of Facts is least able to prove and most
often disputed about — where the vessel physically was, and when it stopped moving.

## Consequences

Run against the seven-day database, the engine produces differentiated, checkable results:

```
245313000   126.5h working   ON DEMURRAGE 54.51h  =  63,598.56 USD
353576000    34.4h working   ON DEMURRAGE  0.09h  =     105.65 USD
231894000    36.1h working   within laytime, 35.92h saved
636025106    46.5h working   within laytime,  0.34h saved
```

- Tanker charters are the tractable case, and this is why the project scoped to tankers in the
  first place: running hours SHINC, commonly 72 hours for load plus discharge, few exceptions. Dry
  bulk's weather working days and SHEX variants are a different and much larger rule set, and this
  engine does not attempt them.
- Despatch is **not** computed. Hours saved are reported because a charterer wants to see them, but
  tanker charters normally do not provide for despatch and inventing a credit the terms do not
  grant would be worse than reporting nothing.
- The next step is M7: ingesting a Statement of Facts into the same timeline shape and reporting
  where it disagrees with AIS. That is the commercially interesting one — demurrage disputes turn
  on contested timelines, and an independent transponder record is usually the missing evidence.
