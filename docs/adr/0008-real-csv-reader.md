# 8. A real CSV reader, not string.Split

Date: 2026-09-11

## Status

Accepted

## Context

The DMA files are comma-separated with a fixed 26-column layout, which invites splitting each line on
commas. That is wrong on real data.

Measured on the 1,717,280-row sample of `aisdk-2026-09-05.csv`: **4,261 rows yield 27 or 28 fields
when split naively** — vessel names containing commas, correctly quoted by the producer. On those
rows a naive split shifts every subsequent column by one or two positions, so speed is read from the
course field, ship type from the name field, and so on.

The corruption is directly visible in a histogram of the ship-type column built by splitting: values
such as `1"`, ` AB"`, `4M"` and ` DB` appear alongside the real categories. Extrapolated, this is
roughly 42,000 silently mis-parsed rows per day.

Re-parsing the same file with a conformant CSV reader leaves **1** malformed row.

## Decision

Parse with a conformant RFC 4180 reader (`Sylvan.Data.Csv`), never `string.Split(',')`. The reader
lives behind the `IAisSource` port (ADR-0003), so the domain rules never see a delimiter.

Field count is validated per row: anything not yielding 26 fields is rejected by rule R1 with the raw
text quarantined, rather than being parsed on a best-effort basis.

Numeric and date parsing uses `InvariantCulture` with an explicit format string (`dd/MM/yyyy
HH:mm:ss`). This is not defensive boilerplate: the development machine runs a Danish locale where the
decimal separator is a comma, and the DMA's own published column documentation shows latitude as
`57,8794` while the actual files use `57.321455`. `InvariantGlobalization` is enabled solution-wide.

## Consequences

- ~42,000 rows per day are parsed correctly that would otherwise have been silently wrong.
- A dependency is added to the CSV adapter only; Core stays dependency-free.
- The fixture deliberately includes quoted-comma rows so this cannot regress unnoticed.
- Header and one data row are asserted at ingest, so a change in producer format or locale fails
  loudly instead of parsing into plausible nonsense.
