# 47. The page shows what the API can now price

Date: 2026-09-21

## Status

Accepted

Crosses the first trigger in ADR-0029 for the second time. ADR-0037 was the first conversation;
this is the one required before widening what that page covers.

## Context

ADR-0037 built a read-only browser page over the derived layer and drew a line:

> **The UI cannot show laytime or reconciliation, and will not pretend to.** Neither has an HTTP
> endpoint, and `reconcile` needs a document uploaded — the write surface this record refuses.

That line rested on a fact which has since changed. ADR-0042 gave laytime an endpoint —
`GET /portcalls/{id}/laytime?allowedHours=&ratePerDay=&turnHours=` — with charter party terms as
query parameters and no upload. ADR-0043 added `GET /ports` and `GET /ports/{wpi}?waitingHours=`.
ADR-0037 anticipated exactly this and declined to pre-empt it:

> Giving them read endpoints (terms as query parameters, no upload) is a possible later step and is
> not taken here.

So the page now omits the two most commercially useful things the API answers, for a reason that no
longer holds. It opens with *Ingest runs* — an operator's concern — and a chartering desk reading it
would not learn that the pipeline prices demurrage at all.

There is a second problem, about audience rather than scope. The static site (ADR-0039) leads with
the navigational-status investigation. That is a finding about data quality; it is not what a
shipping agent came for, and the project has two surfaces telling two unrelated stories.

## Decision

**The page covers laytime and port benchmarks.** It consumes `/portcalls/{id}/laytime`, `/ports` and
`/ports/{wpi}` alongside the endpoints it already reads. ADR-0037's "not on this page" section
shrinks to `reconcile` alone, which still needs a document and so still needs a write surface.

**Charter party terms travel as query parameters on a GET, and that is not a write.** The page's
terms inputs build a URL and fetch it. Nothing is stored, nothing is uploaded, and the server holds
no state between requests. This is recorded explicitly because a form is the shape a write surface
usually arrives in, and the distinction must be legible to the next reader rather than re-derived.
`ShowcasePageTests.ThePageDoesNotReachForPositionsOrAWriteSurface` continues to assert it.

**Everything else in ADR-0037 stands unchanged.** Derived layer only, no `position_report` to a
browser; served same-origin from `wwwroot`, so no CORS; no bundler and no framework. The page grows
two sections, not a toolchain.

**No map.** ADR-0029's expensive consequence — clustering, tiling, a genuinely different read path —
remains uncrossed by scope rather than by engineering, exactly as ADR-0037 left it.

**A priced statement is a local figure; a benchmark is a publishable one.** The site and the page
divide on this and not on convenience:

| | audience | figure | where |
|---|---|---|---|
| Port benchmark | anyone | aggregate, port-level, ranks rather than judges (ADR-0043) | published in the export and on the static site |
| Laytime statement | the operator | names a vessel and a sum of money, on terms supplied at request time | the local page only, never the export |

A demurrage figure published against a named vessel on a public site reads as a commercial claim.
It would also be computed on terms this project invented — 72 hours and 28,000 USD/day are
illustrative defaults, not a charter party — so the number would be both an accusation and a guess.
ADR-0039 already holds that the site reports what a transponder broadcast and accuses nobody; this
keeps that true as the site's subject moves toward money. Only the operator has the real terms,
which makes the local page the only place the figure means anything.

**The static site leads with the laytime story and keeps the investigation.** The navigational-status
work moves under `/findings` intact. It is the evidence for why a self-reported status cannot be
trusted, which supports the pitch instead of being it. Nothing is deleted.

**The export gains port benchmarks and nothing else.** `ExportBuilder` emits the per-port figures
with the sample and exclusions ADR-0043 requires. This is additive to the document ADR-0039 made the
contract, and no priced figure enters it.

**Playwright stays deferred, and this record owes a reason.** ADR-0037 named the admission condition
as "when the UI holds interactive state worth regressing — a filter that composes, a drill-down that
can show the wrong row", and a terms form driving a priced drill-down is closer to that line than
anything before it. It is still declined, because what the form actually does is construct a URL
from three numbers: the logic worth regressing is the URL and the rendering of a refusal, and both
are assertable from the API side without a browser. The condition is not withdrawn — it is admitted
when the page holds state that composes across sections.

## Consequences

**A 422 is an answer, and the page must render it as one.** `/portcalls/{id}/laytime` declines with
`{portCallId, priced: false, refusal, detail}` when the call has no berth phase or its berth
geometry is untrustworthy. That is the pipeline working. The page renders `detail` in the same
register as `≥` and `?` — a stated refusal, not an error — which means its fetch helper can no
longer treat every non-OK status as a failure.

**The showcase test's URL extraction becomes incomplete the moment a URL is parameterised.**
`EveryEndpointTheScriptFetchesAnswers` reads single-quoted literals out of `app.js` precisely so
that no second list can drift. `/portcalls/${id}/laytime` is invisible to it, so the coverage would
be lost silently — the failure the test exists to prevent. The extraction is widened with the
change that introduces the first parameterised URL, and it accepts 200 or 422 there.

**The CLI and the page must agree to the cent.** Both go through `LaytimeAssessor`, so a divergence
would be a bug in one of the two renderers rather than in the calculation — worth checking by hand
against `ais laytime --mmsi <n>` after any change to either.

**The page is now the first thing worth showing someone, which makes it the first thing that looks
wrong when detection changes.** That is a cost accepted deliberately: a surface nobody looks at
cannot report a regression either.
