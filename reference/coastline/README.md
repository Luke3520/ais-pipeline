# Coastline

Runtime reference data: the land outline `ais radar` draws under the vessels. It is not a test
fixture, and nothing derived depends on it. No stop, port call or figure is computed from this
file. It is scenery.

## `danish-waters.csv`

Derived from **Natural Earth 1:10m Physical Vectors, Coastline** (`ne_10m_coastline`, version
5.0.0-pre9).

- **Source**: <https://naciscdn.org/naturalearth/10m/physical/ne_10m_coastline.zip>
  (linked from <https://www.naturalearthdata.com/downloads/10m-physical-vectors/10m-coastline/>)
- **Retrieved**: 2026-10-05.
- **Licence**: public domain. No attribution requirement and no share-alike, which is the same
  reason the port gazetteer is the World Port Index rather than OpenStreetMap.
- **Derivation**: [`scripts/make-coastline.py`](../../scripts/make-coastline.py), standard
  library only. It clips to 3–16°E and 53–60°N, which is every region the radar names plus a
  margin, and simplifies with Douglas–Peucker at 0.005° (about 300 m), well under one braille dot
  at any terminal size. A line that leaves the box and re-enters is split in two rather than
  joined by a chord across the sea.

| column | meaning |
|---|---|
| `line` | Polyline id. Consecutive rows with the same id are joined. |
| `lon` | Degrees east, WGS84. Four decimals (~10 m). |
| `lat` | Degrees north, WGS84. Four decimals. |

57 lines, 3,186 vertices. At 1:10m, Natural Earth leaves out the smallest Danish islands. That's
fine for scenery and would be fatal for anything else, which is why nothing else reads this file.
