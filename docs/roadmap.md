# Roadmap, and what this does not do

## Milestones

| | | |
|---|---|---|
| **M0** | Foundations — solution, CI, ADRs, fixture | ✅ complete |
| **M1** | Ingest — parser, rules R1–R6, schema, idempotency | ✅ complete |
| **M2** | Detection — annotate pass, stops, port calls, seven days | ✅ complete |
| **M3** | Postgres behind the same ports, Docker Compose | ✅ complete |
| **M4** | REST + OpenAPI, GraphQL with DataLoader | ✅ complete |
| **M6** | Laytime engine — terms in, line-item statement out | ✅ complete |
| **M7** | Statement of Facts reconciliation, priced | ✅ complete |
| **M8** | Port resolution — World Port Index gazetteer, named with a distance | ✅ complete |
| **M9** | Read-only browser UI over the derived layer | ✅ complete |
| **M10** | R12, the export contract, and the static site | ✅ complete |
| **M11** | Retention — prune whole periods, archive first | ✅ complete |
| M5 | OpenTelemetry → Prometheus + Grafana, k6 | deferred — infrastructure, and the read side is still small enough to reason about without it |

## Where this is going

Stops now resolve to named ports, and arrival and departure feed the laytime calculation — so the
first two steps this section used to describe are built. What is left is the part a point gazetteer
cannot do: deciding whether a vessel was at a **berth** rather than merely near a port. That needs
harbour extents, and choosing a dataset with them means taking on ODbL share-alike terms for derived
data, which is a decision rather than a task.

The boundary itself — what AIS can and cannot supply a laytime calculation — is stated once, in the
[README](../README.md#what-ais-cannot-tell-you).

## Limitations

- Decoded daily CSVs only — no raw NMEA decoding. Streaming ingestion would be the real-world version.
- Ports come from a **point** gazetteer, not boundaries. A World Port Index record is one
  nominal position near the harbour entrance, so a stop can be named but its membership of a
  port cannot be asserted — which is why every port is reported with its distance and why the
  5 nm figure is a heuristic rather than a finding (ADR-0034). Harbour polygons would replace
  the heuristic with a real containment test.
- Drift and gap thresholds are heuristics, calibrated on a sample and re-calibrated at M2.
- MMSI is not a durable vessel identity; it can be reassigned between voyages.
- Scoped to tankers. The quality report covers the whole feed; the store holds tankers only.
- Pruning is irreversible by design. Past the cutoff, the JSON archive prune writes is the only
  record of a port call, and it cannot be re-derived — the fixes underneath it are gone. It is
  readable with `ais archive`, but it is JSON, not SQL: no joins, no benchmarks, no API over it.
