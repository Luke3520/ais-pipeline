# 35. Select the call the document describes, do not validate a guess

Date: 2026-09-12

## Status

Accepted

Extends ADR-0033, which gated the comparison but deliberately left the selection alone.

## Context

ADR-0033 stopped `reconcile` pricing two unrelated timelines against each other. It did so by
checking the pair it was handed, and it said in its own consequences that selecting the call *by*
the document's window was the obvious next step and was not being taken.

That left the verb in a worse state than it sounds. The AIS side was still chosen by:

```csharp
queries.MostRecentCompletePortCall(sof.Mmsi)
```

so the gate's effect on a historical document — one describing any call but the vessel's latest — was
to refuse it. Correct input, correct document, and a refusal. The gate turned wrong answers into
refusals without making right answers reachable, which is half a fix: the number is no longer wrong,
and the verb no longer works on the documents a demurrage claim actually involves. Statements of
Facts arrive weeks after the event.

## Decision

**The document's window selects the call.** `PortCallsOverlapping(mmsi, from, to)` returns every
call for that vessel sharing time with the document, and `CallSelection.Select` ranks them by how
much. The chosen call is the one sharing the most.

**Overlap is strict, the same bar `CallMatch` applies.** A call ending exactly as the document opens
shares no time with it. Consistency matters here: a selector looser than the gate would pick a
candidate the gate then refuses, which reads as a bug in the gate.

**A tie is refused, not broken.** Two candidates sharing exactly as much time are reported and
nothing is priced. Breaking the tie by recency or by id would be arbitrary, and an arbitrary choice
decides which timeline a money figure is measured against. This is the same position as ADR-0025's
refusal to guess a drift from one reliable fix.

**Candidates that overlap but lose are printed.** Usually one is the genuine match and the other a
neighbouring visit clipped at the edge; sometimes it is a vessel that called twice in a week. The
reader can tell and the pipeline cannot, so the evidence goes in the output rather than being
dropped — rule 2 applied to a selection rather than to a row.

**Incomplete calls are candidates.** Excluding them would report "no call matches this document" for
a document whose call the store holds but could not measure, which names the wrong problem. The
trust gates downstream — no berth phase, untrustworthy berth geometry — already refuse with a message
that says which it is, and those messages are more useful than a false absence.

## Consequences

`reconcile` works on historical documents, which is what the verb is for.

`MostRecentCompletePortCall` is no longer used by `reconcile`. It stays: `laytime` uses it, and for
`laytime` "the vessel's latest finished call" is the actual question rather than a stand-in for one.

The selection is only as good as the document's timestamps. A Statement of Facts with the wrong date
— a typo in a year, a local time recorded as UTC — selects the wrong call or no call, and the output
names the windows so that shows up as a visible mismatch rather than a quiet one. AIS cannot detect
a transcription error in a document; it can only show what it was compared against.

Two calls at the same port within one document's span still cannot be separated by time alone. The
port check from ADR-0034 does not help — both calls are at the same port — so this stays a tie, and
ties refuse.
