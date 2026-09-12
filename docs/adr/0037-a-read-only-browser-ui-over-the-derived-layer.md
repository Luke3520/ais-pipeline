# 37. A read-only browser UI, over the derived layer only

Date: 2026-09-12

## Status

Accepted

Crosses the first trigger in ADR-0029, which required this record before any code was written.

## Context

ADR-0029 made the architecture provisional in named ways and put **a browser UI** first on the list.
Its warning was specific: 5.4M fixes cannot go down a wire as JSON, so a UI means clustering, tiling
or vector tiles — "a genuinely different read path from anything here now" — plus static hosting,
CORS, and Playwright.

That warning is correct about a **position map**. It is not correct about every UI, and the numbers
say so:

| | rows | as JSON |
|---|---|---|
| `position_report` | 5,368,195 | the read-path problem |
| `stop_event` | 809 | 353 KB |
| `port_call` | 362 | 141 KB |

The entire derived layer for seven days — every stop, every port call, every attributed port, every
waiting and working hour — is **494 KB uncompressed**. It fits in one response. The expensive half of
the trigger is entirely a property of showing raw positions.

There is also a better subject. The distinctive output of this pipeline is not where ships were; it
is what the pipeline **refused to say**: `>=2.7` for a duration whose extent is unknown, `~Lysekil
9.2 nm` for near-but-not-alongside, `R10 299 of 809 stops`, and a reconciliation that names two
windows and declines to price them. Those are tables and text.

## Decision

**The UI covers the derived layer and not `position_report`.** No positions are served to a browser,
so no clustering, tiling or vector tiles, and the read path is unchanged: the page consumes
`/runs`, `/quality`, `/stops` and `/portcalls` exactly as they already exist. ADR-0029's expensive
consequence is avoided by scope rather than by engineering.

**Served from the API's own `wwwroot`, so there is no CORS.** ADR-0029 listed CORS as a cost; it is a
cost of a *separately hosted* client. Same-origin static files remove the requirement instead of
configuring it, and a cross-origin dev server is the thing that would bring it back.

**No bundler and no framework.** Plain HTML, CSS and JavaScript. A build toolchain for a handful of
tables over a 494 KB read model would be the largest thing in the repository, and `dotnet` remains the
only toolchain a contributor needs.

**No write surface, explicitly.** No Statement of Facts upload, no charter party form. That is
ADR-0029's second trigger, and it makes authentication non-deferrable the moment commercially
sensitive data arrives (ADR-0017). `laytime` and `reconcile` stay CLI verbs.

**Playwright stays deferred, and the condition is named.** ADR-0027 deferred it because there was no
UI; there is one now, which removes that reason but does not by itself supply one. The page is
read-only tables over endpoints the contract suite already covers, so what a browser test would add
is coverage of rendering, not of behaviour. Admit it when the UI holds interactive state worth
regressing — a filter that composes, a drill-down that can show the wrong row. Until then, the
API-side test asserts the page is served and that every endpoint it depends on answers.

## Consequences

**The UI cannot show laytime or reconciliation, and will not pretend to.** Neither has an HTTP
endpoint, and `reconcile` needs a document uploaded — the write surface this record refuses. So the
two most interesting outputs of the project remain CLI-only, and the page says so rather than
quietly omitting them. Giving them read endpoints (terms as query parameters, no upload) is a
possible later step and is not taken here.

The position map remains unbuilt and ADR-0029's trigger for it remains uncrossed. Nothing in this
record makes it easier or harder; it deliberately does not lay groundwork for it, because
speculative groundwork for a read path nobody has designed is how drift happens.

A second static-hosting surface now exists alongside Scalar's API reference. Both are same-origin
and neither is authenticated, which is consistent with ADR-0017's deferral for a single-operator
read-only tool and would have to be revisited with the first write.
