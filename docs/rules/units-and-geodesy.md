---
paths: src/AisPipeline.Core/Geo/**, src/AisPipeline.Core/Detection/**, src/AisPipeline.Adapters.Csv/**
---

# Units and geodesy

Getting a unit wrong here produces a plausible number rather than an error, which is why this is a
rule and not a convention.

## The units

| Quantity | Unit | Suffix |
|---|---|---|
| Distance | nautical miles | `_nm` |
| Speed | knots | `_kn` |
| Time instant | UTC, ISO 8601 | `_utc` |
| Duration | hours (fractional) | `_hours` |
| Position | decimal degrees | `_lat` / `_lon` |

**1 nm = 1852 m exactly.** Declare it once as a named constant; never inline `1852` or `3440.065`
at a call site.

Every variable, parameter, column and record field carrying a physical quantity **names its unit in
its identifier**. `maxDrift` is a defect waiting to happen; `maxDriftNm` is not.

## Haversine

Returns nautical miles. Earth radius is a named constant in nautical miles, so the conversion
happens once rather than at each call site.

Implied speed between two fixes is `distanceNm / hours`. **Guard `dt == 0` before dividing** —
444 tanker rows in a 1.7M-row sample share a vessel-second with a different position, so this
divides by zero on real data rather than in theory (ADR-0021).

## Parsing

Timestamps are `dd/MM/yyyy HH:mm:ss` in **UTC**, parsed with an explicit format string and
`InvariantCulture`. Numbers likewise.

This is not defensive boilerplate. The development machine runs a Danish locale where the decimal
separator is a comma, and the DMA's own published column documentation shows latitude as
`57,8794` while the actual files use `57.321455`. `InvariantGlobalization` is enabled solution-wide
(ADR-0008).

## The CSV reader's comment character

The DMA header line begins `# Timestamp`. **Sylvan's default `Comment` character is `#`**, so at
the default the reader treats the real header as a comment, promotes the **first data row** to be
the header, and silently drops that row from every file parsed.

Always construct the reader with `new CsvDataReaderOptions { Comment = '\0' }`. This is a silent
loss, not an error: the row count is off by one per file and the column names become a timestamp.
Caught by `FixtureIntegrityTests` before any parser existed; that test is the regression guard and
must not be weakened to match a changed reader default.

## Rounding

**Never round a timestamp.** Laytime is counted to the minute and rounding compounds across line
items — this is the constraint that keeps M6 reachable.

Round for display only, never before storage or arithmetic. Distances and durations are stored at
full precision.

## Thresholds are constants, not literals

Every threshold is a named constant with its unit in the name and its origin in a comment or ADR:
entry and exit speed, minimum stop duration, coverage-gap length, teleport speed, berth drift.
They are heuristics, several are empirically calibrated (ADR-0020), and all of them will be retuned
against a larger window. An inline literal is a threshold nobody can find.
