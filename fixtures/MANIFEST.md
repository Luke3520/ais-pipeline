# fixtures/sample.csv

Real rows taken verbatim from `aisdk-2026-09-05.csv` (DMA open AIS data,
`http://aisdata.ais.dk/`), kept in original file order so the time-sorted assumption
holds. Every row is here for a reason: this file is the evidence the test suite runs
against.

**744 rows.** Selected properties:

| Rows | Why it is in the fixture |
|---:|---|
| 60 | tanker stationary 159 min while reporting "Under way using engine" -- rule R10, the project's headline finding, which the fixture previously did not contain |
| 470 | tanker transitioning moving<->stopped (state machine: 3 hysteresis crossings) |
| 138 | tanker moored alongside (low drift, berth population) |
| 33 | tanker at anchor (higher drift, anchorage population) |
| 7 | R2 exact duplicate on the natural key |
| 6 | quoted field containing a comma (breaks naive split) |
| 5 | R3 same-second conflicting position (receiver disagreement) |
| 4 | R4 lat-91 position sentinel |
| 4 | R5 SOG unavailable (blank) |
| 4 | tanker MMSI emitting Undefined ship type (two-pass recovery) |
| 4 | non-tanker traffic: base station |
| 4 | non-tanker traffic: cargo vessel |
| 3 | tanker static data present (IMO + name) |
| 3 | R9 missing static identity (IMO 'Unknown') |
| 3 | non-tanker traffic: AtoN |
| 3 | non-tanker traffic: class B |

Counts overlap where one row demonstrates several properties.

## Stops it produces

With thresholds 0.5/1.0 kn, 30 min minimum, 60 min gap, 0.01 nm berth cutoff:

| MMSI | Duration | max drift | Phase | Vessel's own status |
|---|---:|---:|---|---|
| 563259900 | 159.0 min | 0.1032 nm | anchorage | At anchor |
| 255916087 | 159.0 min | 0.0016 nm | berth | Moored |
| 244678000 | 131.8 min | 0.0789 nm | anchorage | At anchor |
