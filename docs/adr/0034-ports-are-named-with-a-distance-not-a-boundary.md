# 34. A port is named with a distance, never claimed as a boundary

Date: 2026-09-12

## Status

Accepted

## Context

A stop centroid was coordinates. `port_call` recorded 55.68N 12.61E and nothing said which port that
was, which left two things broken:

- **`reconcile` could not check the port.** A Statement of Facts names its port; ADR-0033 had to
  record `PortWasChecked = false` and match on times alone, because AIS held no port identity to
  compare against.
- **Repeat visits were invisible.** Rounding centroids to a 0.01° grid split the two busiest sites
  in the seven-day window across cell boundaries — 13 calls and 11 calls at what is one place.

The obvious design is a radius: pick a port dataset, match a centroid to the port within N nm, done.
The measurement says that design cannot work.

### What the data says

The World Port Index (NGA Pub. 150) gives 145 ports inside the feed's coverage. Distance from each
of the 362 detected port calls to its nearest WPI port:

| | nearest-port distance |
|---|---|
| p10 | 0.36 nm |
| p25 | 1.37 nm |
| p50 | 3.07 nm |
| p75 | 7.06 nm |
| p90 | 11.45 nm |
| p99 | 20.98 nm |
| max | 57.20 nm |

**Continuous, with no gap to cut at.** And the busiest clusters show why:

| site | calls | nearest WPI port | distance |
|---|---|---|---|
| 57.69, 11.88 | 13 | Goteborg | 2.67 nm |
| 57.69, 11.87 | 11 | Goteborg | 2.96 nm |
| 57.43, 10.56 | 12 | Frederikshavn | 0.38 nm |
| 54.37, 10.14 | 10 | Kiel | 3.21 nm |
| 54.16, 12.13 | 6 | Rostock | 3.60 nm |
| 56.15, 10.22 | 4 | Arhus | 0.11 nm |
| 55.81, 10.74 | 4 | Kalundborg | 14.35 nm |

The first six are vessels alongside. The last is a mid-Kattegat anchorage that is not in Kalundborg
in any sense. A WPI record is a single nominal point near the harbour entrance, so the offset to an
actual berth scales with the size of the port: Arhus resolves at 0.11 nm and Rostock at 3.60 nm, both
correctly. Any radius tight enough to exclude the 14.35 nm anchorage also excludes Kiel and Rostock.

This is not a defect in the dataset. It is what a point gazetteer is. Only a dataset carrying port
*extents* — harbour polygons — could answer "is this vessel inside port X", and choosing one means
taking on ODbL share-alike terms for derived data, which is a larger decision than this feature needs.

## Decision

**Store the nearest port with its distance. Never store a membership claim.** `port_call` gains the
port's WPI number, name, country and the distance in nautical miles. The distance is not diagnostic
metadata — it is half the answer, and a name without it makes an anchorage at sea indistinguishable
from a berth.

**`PlausiblyAtPortNm = 5.0`, and it is called a heuristic everywhere it appears.** It admits every
berth observed in the window (max 3.60 nm) and excludes the nearest offshore anchorage (14.35 nm).
Anything from roughly 4 to 13 nm separates those observations; 5 nm sits just above the berths with
room for a port larger than any here. A call near the boundary is a question, not an answer.

**This threshold will not improve with more AIS data.** Unlike ADR-0020's berth threshold, which
seven days of observation moved by a factor of thirty, this one describes how NGA places reference
points. More vessels will not refine it. Saying so now prevents someone later mistaking it for a
measurement that simply needs more samples.

**`TooFarToNameNm = 25.0`: beyond it, no port is named at all.** The four calls past 21 nm are stops
in open water; the farthest sat 57.20 nm from Egersund. Naming that "Egersund" would be attribution
by arithmetic. A stop far from anywhere gets no port, which is a fact about the stop rather than a
gap in the gazetteer — the same position `StoredStop.MaxDriftNm` takes when too few fixes survive.

**The gazetteer is committed, with its derivation.** `reference/ports/` rather than
`docs/reference/`, which is deny-by-default because the material there is not ours to redistribute.
WPI is a US federal government work in the public domain, so it is ours to commit, and the README
records the source URL, the retrieval date, the bounding box and the columns kept.

## Consequences

`reconcile` can compare the port a document names against the port AIS attributes, so ADR-0033's
`PortWasChecked = false` becomes a real check. It is a check on a name, though — WPI's `Main Port
Name` spelled "Arhus" and "Goteborg" will not string-match a document saying "Århus" or
"Gothenburg", and normalising port names across languages and transliterations is its own problem.
The comparison reports what both sides say rather than asserting a match.

Repeat visits become countable: two calls at the same WPI number are at the same port, whatever the
grid would have said.

The 5 nm heuristic is the weakest number in the pipeline. It is one constant, in one place, named
after what it means, and the README carries the distribution it came from — which is the most that
can honestly be done with a point gazetteer.

A Danish-waters feed needed a gazetteer covering five countries: the three busiest sites in the
window are Goteborg, Kiel and Rostock, none of them Danish. A Denmark-only port list would have
resolved none of them.
