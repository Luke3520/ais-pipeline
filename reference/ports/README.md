# Port gazetteer

Runtime reference data: the list of ports a stop centroid is matched against. Not a test fixture
and not documentation — `detect` reads this file.

It lives here rather than under `docs/reference/`, which is deny-by-default because the material
there is not ours to redistribute. This is, so it is committed.

## `wpi-baltic-north-sea.csv`

Derived from the **World Port Index (NGA Pub. 150)**, published by the US National
Geospatial-Intelligence Agency.

- **Source**: <https://msi.nga.mil/api/publications/download?key=16920959/SFH00000/UpdatedPub150.csv&type=view>
  (linked from <https://msi.nga.mil/Publications/WPI>)
- **Retrieved**: 2026-09-12. Full file 3,807 ports, 3.5 MB.
- **Licence**: a work of the US federal government, in the public domain. No attribution
  requirement and no share-alike, which is why this one and not OpenStreetMap's harbour polygons —
  ODbL's share-alike terms on derived data are a decision this project does not need to take yet.
- **Derivation**: rows whose latitude is 53.0–59.0 and longitude 6.0–15.0, which is the DMA feed's
  coverage plus a margin; 145 ports survive. Columns reduced to the seven below and sorted by
  `wpi_number` so a refresh produces a readable diff. Nothing else is altered — coordinates are
  carried at source precision.

| column | meaning |
|---|---|
| `wpi_number` | NGA's stable port id. The join key to the published index. |
| `port_name` | `Main Port Name`. |
| `alternate_name` | `Alternate Port Name`, often the local spelling. Empty for most. |
| `un_locode` | UN/LOCODE where NGA has one. Empty for some. |
| `country` | NGA's `Country Code` field, which holds a country *name*, not a code. |
| `latitude_deg` | Degrees north, WGS84. |
| `longitude_deg` | Degrees east, WGS84. |

145 ports: 67 Danish, 36 German, 25 Swedish, 12 Norwegian, 3 Polish, 2 Dutch. The spread matters —
the feed's busiest stop clusters include Goteborg, Kiel and Rostock, so a Denmark-only list would
have missed the three most-visited sites in the data.

## What it gives us

**Names for coordinates, and a stable id per port.** The two busiest clusters in the seven-day
window sat either side of a 0.01° grid boundary and counted as separate places; both resolve to
Goteborg. That is the whole reason a gazetteer beats rounding.

## What it does not give us

**Berth positions, or any notion of a port's extent.** A WPI record is one point — a nominal
reference position, usually near the harbour entrance — and the offset from it to an actual berth
scales with the size of the port. Measured against the 362 port calls detected in the window:

| | nearest-port distance |
|---|---|
| p10 | 0.36 nm |
| p25 | 1.37 nm |
| p50 | 3.07 nm |
| p75 | 7.06 nm |
| p90 | 11.45 nm |
| max | 57.20 nm |

The distribution is **continuous, with no gap to cut at**. Vessels genuinely alongside sit at
0.11 nm (Arhus), 2.67 nm (Goteborg), 3.21 nm (Kiel) and 3.60 nm (Rostock) — so any radius tight
enough to exclude a mid-Kattegat anchorage 14.35 nm off Kalundborg also excludes half the real
berths. There is no threshold that answers "is this vessel in port X", and this file cannot be made
to answer it.

So the pipeline stores the nearest port **with its distance** and does not claim membership. See
`docs/adr/0034-*` for what follows from that.

## What corroborates the 5 nm heuristic

Measured after ADR-0034 was written, from a signal the threshold knows nothing about. Whether a
stop is a berth or an anchorage is decided by drift geometry alone (ADR-0020), so berth hours are an
independent test of whether the distance band separates anything real:

| distance band | calls | with berth time | share |
|---|---|---|---|
| within 5 nm | 238 | 142 | 59.7% |
| beyond 5 nm | 119 | 20 | 16.8% |
| no port named | 5 | 2 | 40.0% |

A call within 5 nm of its port is **three and a half times** more likely to have involved time
alongside. Two unrelated methods agree, which is the strongest thing available here short of a
dataset with real port extents.

It is not a clean split and is not claimed as one. 16.8% of calls beyond 5 nm did berth — large
ports whose quays lie further from the reference point than the threshold allows — and 40% of calls
within 5 nm never berthed, which is what an anchorage just outside a harbour looks like. The
heuristic separates; it does not decide.
