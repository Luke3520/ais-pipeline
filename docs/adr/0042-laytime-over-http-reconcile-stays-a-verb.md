# 42. Laytime over HTTP; reconcile stays a CLI verb

Date: 2026-09-13

## Status

Accepted

## Context

The two most commercially useful things this project computes — a laytime statement and a
reconciliation against a Statement of Facts — were reachable only by typing a command. Everything
that *is* exposed over HTTP (vessels, stops, port calls, the quality report) is descriptive; the
two outputs that carry a money figure were not exposed at all.

Demurrage is the reason anyone would care. Rates run to tens of thousands of dollars a day, the
Statement of Facts is prepared by the port agent and signed by parties who all have an interest in
the outcome, and there is no independent record of what the vessel physically did. That is the gap
this project fills, and a CLI verb fills it for one person at one terminal.

The two are not equally exposable, and the difference is not about effort.

## Decision

**`GET /portcalls/{id}/laytime` prices one call, with the charter party terms as query parameters.**
Allowed hours, demurrage rate, turn time, currency and an optional notice of readiness are a handful
of numbers and one timestamp; they fit in a URL, nothing is stored, and the request is idempotent
and cacheable.

**`reconcile` stays a CLI verb.** It needs a Statement of Facts *document*, and accepting one is
the write surface ADR-0029 names as its second trigger. The trigger is about commercially sensitive
data arriving, not about whether it is persisted — a POST that reconciles a document and forgets it
has still received a signed commercial record over an unauthenticated connection, and ADR-0017's
deferral of authentication assumed nothing of the sort would happen. Exposing it is a decision about
authentication and transport, and it is not this one.

**A refusal is a value, not a message.** `LaytimeAssessor` returns either a statement or a named
refusal — no berth phase, or berth geometry the pipeline does not stand behind — so both surfaces
render the same outcomes. The endpoint answers **422** rather than 400: the request is well formed
and the call exists, what is missing is evidence, and the body names which. A refusal nobody can
read is indistinguishable from a server fault.

**The response carries the line items, not just the total.** Every hour between commencement and
completion belongs to exactly one line with a reason attached. A total nobody can decompose is a
total nobody can dispute, and disputing it is the entire point of the artefact.

**When no notice of readiness is supplied, arrival is substituted and the response says so.** AIS
cannot observe a notice — no transponder emits one — and `noticeOfReadinessIsAssumed` travels with
the figure rather than being a footnote on a page somewhere (ADR-0030).

## Consequences

The CLI and the API now share `PortCallRebuilder` and `LaytimeAssessor` instead of carrying their
own copies of the same sequence. There were already two copies in the CLI alone and they had
drifted: one dropped the port attribution, so `reconcile` reported "AIS named no port for this call"
for a call whose port was in the row it had been handed. A third copy was about to be written.

**Two adapter methods were not on the port and nobody noticed**, because `ReadConnection.OpenQueries`
returns the concrete `SqlAisQueries`, so the CLI had been calling straight past `IAisQueries`. The
API takes the interface through DI, which is what surfaced it. Both are now declared on the port,
and the leak is worth recording: returning a concrete adapter from a helper quietly makes the port
optional, and ADR-0003's rule only holds where something forces the abstraction.

Writing the endpoint reintroduced the ADR-0028 defect — it looked the call up by filtering a page of
`ListPortCalls`, which ranks by duration before applying its limit, so a call outside the page would
have been reported as not existing. It works at 362 calls and fails silently later. `GetPortCall`
replaces it.

The endpoint prices any call, including an incomplete one whose hours are lower bounds. It reports
`isComplete` and the caller decides; refusing would withhold a figure that is correct as far as it
goes, and a bound is what this project publishes everywhere else.
