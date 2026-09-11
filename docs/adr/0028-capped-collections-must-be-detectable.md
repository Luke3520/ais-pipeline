# 28. A capped collection must be detectable, and must keep the newest

Date: 2026-09-11

## Status

Accepted

## Context

The GraphQL surface resolves a vessel's port calls through a batched DataLoader with a per-vessel
cap of 50, so one query cannot demand unbounded work. Two lanes of the review found the same defect
in it independently.

The cap was applied as `ORDER BY mmsi, arrived_utc` followed by `Take(50)`. Two things follow, and
both are wrong:

**The truncation was undetectable.** The field returned a plain list with no total and no flag. A
client summing `waitingHours` and `workingHours` across `vessel.portCalls` could not distinguish a
vessel that made exactly fifty calls from one that made two hundred and had a hundred and fifty
dropped. The sum would simply be short, with nothing in the response to say so.

**The truncation kept the wrong half.** Ascending order with a `Take` retains the *earliest* calls
and discards the most recent — the opposite of what a caller asking for a vessel's port calls
wants.

It is dormant today: the busiest vessel across the seven-day window made 11 port calls. Extrapolated
to a year that is roughly 570, so the cap will bite. This is the shape the project keeps meeting —
correct on the common case, silently wrong on the case it will eventually see — and CLAUDE.md rule 2
already names the principle: nothing leaves without evidence.

## Decision

**Order most recent first**, so a cap drops the oldest rather than the newest.

**Expose `portCallCount`**, an uncapped total resolved through its own batched DataLoader. A client
can always compare the returned length against it, so a truncated list is detectable rather than
merely bounded. The count is batched too, so making the cap visible costs one query per request
rather than one per vessel — it must not reintroduce the N+1 it exists to reveal.

The schema description states the ceiling rather than implying there is none.

## Consequences

- A truncation is now a *limit* rather than a *wrong number*: the information needed to detect it is
  always present.
- Tests live at the query layer rather than the API layer. The committed fixture spans 2h40m, so it
  cannot contain a vessel with two port calls — those need a twelve-hour gap or ten nautical miles
  of separation — and the API-level test could only ever pass vacuously. It asserted that it had
  found a case to check, and failed rather than passing on nothing, which is how this was noticed.
  The query-layer tests construct the case through the store port instead.
- The same cap-and-count pattern applies to `GetStopsForVessels` if a nested `stops` field is ever
  added. It is not exposed in the schema today, so it is not yet a defect.
- The write path of `UtcDateTimeHandler` had the mirror image of the bug its own doc comment warns
  about for reads: plain `ToUniversalTime()` on a `Kind=Unspecified` value assumes local time and
  shifts by the host's offset. Both directions now go through the same conversion. Unreachable
  today — `ListFixes` is not wired to a route — but it would have been inherited silently by the
  first change that exposed one.
