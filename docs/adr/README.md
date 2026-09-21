# Architecture Decision Records

Decisions are recorded here in [Michael Nygard's format](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions):
**Title · Status · Context · Decision · Consequences**.

This is the same discipline the pipeline applies to its data. A position report that cannot be
traced back to a source file and line number is a number you cannot trust; an architecture you
cannot trace back to a reason is a structure you cannot safely change. ADRs are provenance for
the design.

## Rules

- **One decision per record.** Numbered sequentially, never renumbered.
- **Status** is `Proposed` → `Accepted` → `Superseded by ADR-NNNN`.
- **Records are never edited after acceptance and never deleted.** A reversed decision gets a new
  record that supersedes the old one. What was believed, and when, is the artifact.
- **Cite evidence.** Where a decision rests on measured data, the record carries the number and
  how it was obtained.
- **Record rejected alternatives with their reasoning.** Knowing why MongoDB was not used is worth
  more than a record adopting it.

## Index

| # | Title | Status |
|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted |
| [0002](0002-modular-monolith-over-microservices.md) | Modular monolith over microservices | Accepted |
| [0003](0003-hexagonal-architecture.md) | Hexagonal architecture with a no-I/O core | Accepted |
| [0004](0004-relational-source-of-truth.md) | Relational store as source of truth | Accepted |
| [0005](0005-idempotent-ingest-natural-key.md) | Idempotent ingest via a natural key | Accepted |
| [0006](0006-quarantine-versus-flag.md) | Quarantine versus flag | Accepted |
| [0007](0007-two-pass-ingest-vessel-identity.md) | Two-pass ingest filtered by vessel identity | Accepted |
| [0008](0008-real-csv-reader.md) | A real CSV reader, not string.Split | Accepted |
| [0009](0009-no-event-sourcing.md) | No event sourcing | Accepted |
| [0010](0010-stop-detection-thresholds.md) | Stop detection thresholds and hysteresis | Accepted |
| [0011](0011-coverage-gaps-break-stops.md) | Coverage gaps break stops | Accepted |
| [0012](0012-split-duplicate-counters.md) | Split duplicate counters | Accepted |
| [0013](0013-drop-redundant-index.md) | Drop the redundant vessel-time index | Accepted |
| [0017](0017-defer-authentication.md) | Defer authentication until there is something to protect | Accepted |
| 0014–0016, 0018–0019 | *Never written. Numbers reserved during planning for decisions that were recorded as 0026 and 0027 when they were actually made. Left as a gap rather than renumbered: ADR numbers are identifiers, and renumbering would break every citation pointing at them. `scripts/check-adr-citations.sh` fails the build if source cites a number that does not resolve.* | — |
| [0020](0020-berth-drift-threshold.md) | Berth/anchorage drift threshold, calibrated | Superseded by 0024 |
| [0021](0021-sog-consistency-and-rule-ordering.md) | SOG consistency rule and annotate-before-detect ordering | Accepted |
| [0023](0023-teleport-needs-a-distance-gate.md) | Teleport rule needs a distance gate | Accepted |
| [0024](0024-berth-threshold-scales-with-duration.md) | Berth threshold scales with stop duration | Accepted |
| [0025](0025-r11-distance-gate-and-geometry-trust.md) | R11 distance gate; a stop refuses to classify untrustworthy geometry | Accepted † |
| [0026](0026-postgres-adapter-and-no-hypertable.md) | Postgres adapter behind the same ports; no hypertable | Accepted |
| [0027](0027-api-surface-and-n-plus-one.md) | REST for operations, GraphQL for analysis; N+1 proved by counting | Accepted |
| [0028](0028-capped-collections-must-be-detectable.md) | A capped collection must be detectable, and must keep the newest | Accepted |
| [0029](0029-when-to-revisit-the-architecture.md) | When to revisit the architecture | Accepted |
| [0030](0030-laytime-engine.md) | A laytime engine, and the boundary of what AIS can prove | Accepted |
| [0031](0031-reconciling-a-statement-of-facts.md) | Reconciling a Statement of Facts against AIS | Accepted |
| [0022](0022-three-lane-review-harness.md) | Three-lane review harness | Accepted |
| [0032](0032-the-quality-report-counts-both-halves.md) | The quality report counts both halves | Accepted † |
| [0033](0033-a-document-must-be-matched-to-the-call-it-describes.md) | A document must be matched to the call it describes | Accepted |
| [0034](0034-ports-are-named-with-a-distance-not-a-boundary.md) | A port is named with a distance, never claimed as a boundary | Accepted |
| [0035](0035-select-the-call-the-document-describes.md) | Select the call the document describes, do not validate a guess | Accepted |
| [0036](0036-r10-is-reported-separately-because-it-counts-something-else.md) | R10 is reported separately, because it counts something else | Accepted |
| [0037](0037-a-read-only-browser-ui-over-the-derived-layer.md) | A read-only browser UI, over the derived layer only | Accepted |
| [0038](0038-r12-the-mirror-of-r10.md) | R12 — a stationary claim contradicted by the vessel's own speed | Accepted |
| [0039](0039-an-export-contract-for-a-static-site.md) | An export contract, so the site never touches the database | Accepted |
| [0040](0040-store-the-rest-of-the-feed.md) | Store the rest of the feed | Accepted |
| [0041](0041-r13-a-stale-eta-goes-forward-not-backward.md) | R13 — a stale ETA goes forward, not backward | Accepted † |
| [0042](0042-laytime-over-http-reconcile-stays-a-verb.md) | Laytime over HTTP; reconcile stays a CLI verb | Accepted |
| [0043](0043-port-benchmarks-carry-their-sample.md) | A port benchmark carries the sample it rests on | Accepted |
| [0044](0044-retention-and-what-it-collides-with.md) | Retention: attempted, and rejected for now | Superseded by ADR-0045 |
| [0045](0045-prune-whole-periods-archive-first.md) | Prune whole periods, and archive before deleting | Accepted |
| [0046](0046-an-archive-nothing-reads-is-not-an-archive.md) | An archive nothing reads is not an archive | Accepted |

### † Measurement corrected, 2026-09-12

Both records state in passing that **R6 has never fired**. Over the seven-day window it rejects 65
rows and flags 22. The claim was true of the 1.7M-row sample it was measured on, was repeated as
though it held generally, and could not be checked until `ais quality` existed to print the counter
(ADR-0032 itself).

Neither decision is affected — ADR-0025's distance gate and ADR-0032's two-column report both stand,
and neither rests on R6 being dormant. So neither is superseded, and **neither has been edited**:
what was believed, and when, is the artifact. This note is the correction, and it sits here because
the index is the one path every reader of these records passes through.

The rule that would have caught it is now in
[`docs/rules/quality-rules.md`](../rules/quality-rules.md): a dormancy claim has to name the window
it was measured on.

### † Measurement corrected, 2026-09-13 (ADR-0041)

That record puts the empty band in the ETA distribution at **30–90 days**, measured on a
three-million-line slice of one day, and says explicitly that the claim to re-check after a full
rebuild is the gap rather than the counts. Re-checked against all seven days, the gap is narrower:
fixes do occur at 30, 31, 32, 33 and 41 days ahead, and 5 occur at 77. The run of days carrying no
fix at all is **42–76**.

The decision is unaffected. The threshold of 60 sits inside the empty run on both measurements, so
it classifies identically and the reasoning — that its exact value cannot matter — still holds on
the narrower band. The record is not superseded and has not been edited; the numbers it states for
the band are simply drawn from a slice, as it warned.
