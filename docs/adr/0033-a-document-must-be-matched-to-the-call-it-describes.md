# 33. A document must be matched to the call it describes

Date: 2026-09-12

## Status

Accepted

## Context

`reconcile` picked the AIS side of the comparison like this:

```csharp
queries.MostRecentCompletePortCall(sof.Mmsi)
```

MMSI, and nothing else. The Statement of Facts carries a port name and a span of timestamps; both
were ignored in matching. `sof.Port` was parsed from the document, printed in the header, and
compared to nothing.

So a statement for a call in March, fed to a store holding the vessel's September call, would
reconcile. `TimelineReconciler` would compare "all fast" on the document against a berth phase six
months away, find a discrepancy of several thousand hours, price it at the demurrage rate, and print
the figure in the same format it prints an honest one. ADR-0031 went to some trouble to make that
figure trustworthy — the difference between two calculations rather than the sum of priced deltas —
and then handed it two timelines nobody had checked were about the same event.

This is the worst shape available to this project: not a wrong number, but a wrong number with an
audit trail behind it. Every provenance guarantee in rule 1 holds perfectly while the comparison is
meaningless.

It was hidden because the committed fixture was deliberately built against a real detected port call
(`f82c58d`), so it matches by construction. The defect bites the first time anyone reconciles a
document for anything other than the vessel's latest complete call — which is the normal case as
soon as a vessel has more than one.

## Decision

**`CallMatch.Evaluate` decides whether the two describe the same visit, and the reconciler refuses
when they do not.** Checked before anything is computed, so there is no path that produces a price
from a mismatched pair.

**The test is interval overlap, not a proximity threshold.** A document and the call it describes
necessarily share time: a Notice of Readiness is tendered at the anchorage, which is inside the
call, and the last line comes off at its end. Overlap needs no calibrated constant — which matters,
because every threshold this project guessed at has since had to move, ADR-0020's by a factor of
thirty. A tuned "within N hours" would be one more number with no measurement behind it.

**Touching windows do not count.** Overlap must be strictly positive. A document ending exactly
when a call begins has zero shared time, and zero is not evidence; the boundary has to fall on the
refusing side or a document for the immediately preceding call passes.

**The port name is carried, not checked, and the result says so.** AIS has no port identity — a
stop is a centroid, and nothing in the store says which port a centroid sits in. `PortWasChecked`
is `false` and the CLI prints that the port was not compared. Half the evidence a human would use
is unavailable, and a reader deciding how much to trust a match should be told that rather than
left to infer it from its absence.

**The verdict travels on the `Reconciliation` result.** The demurrage difference is meaningless if
the pair is mismatched, so the qualifier sits on the same object as the figure it qualifies — the
same reason `StoredStop.DurationHours` is null rather than qualified in a comment.

## Consequences

A document for a call that is not the vessel's most recent complete one is now refused with both
windows named, rather than silently priced. The refusal says what to do: ingest the window the
document covers, then run `detect`.

`reconcile` still selects by "most recent complete call", so a correct document can be refused when
the store simply does not hold its call yet. That is the right failure direction — refusing a
comparison the data cannot support, rather than making one it cannot support — but it means the verb
cannot yet reconcile historical documents against a populated store. Selecting the call *by* the
document's window, rather than validating the one the query happened to return, is the obvious next
step and is deliberately not taken here: it changes which call is compared, not merely whether the
comparison is allowed.

Overlap is a weak test where a vessel makes two calls at the same port in quick succession with a
document spanning both. It cannot distinguish them, and it is not claimed to. The port check is
what would, and that waits on stops resolving to ports.
