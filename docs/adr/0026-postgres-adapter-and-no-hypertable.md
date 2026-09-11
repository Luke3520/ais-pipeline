# 26. A Postgres adapter behind the same ports, and no hypertable

Date: 2026-09-11

## Status

Accepted

## Context

ADR-0004 chose a relational store and staged it: SQLite through M2, Postgres at M3, "with
TimescaleDB considered for the fix table as the window grows". It also warned that the migration
is only an adapter swap if no SQLite-specific SQL leaks into Core.

That warning is now testable rather than aspirational.

## Decision

**`PostgresAisStore` implements `IAisStore`, and nothing in Core changed.** The two adapters differ
where the dialects genuinely differ — `ON CONFLICT DO NOTHING` for `INSERT OR IGNORE`, `RETURNING
id` for `last_insert_rowid()`, `TIMESTAMPTZ` for an ISO string, `LEAST`/`GREATEST` for SQLite's
two-argument `MIN`/`MAX` — and nowhere else.

The schema is written out twice rather than templated from one string. Identity generation,
conflict handling and the type of a timestamp are separate decisions in each engine, and hiding
them behind substitution would make both versions harder to read while pretending a portability
that does not exist.

**The integration suite runs every assertion against both adapters.** `AdapterCoverageTests` fails
in CI if Postgres was skipped there, because a suite that silently drops an adapter looks exactly
like a suite that covered it — the same reasoning as failing on zero discovered tests.

**Bulk insert goes through a staging table.** `COPY` is Postgres's fast path but cannot express
`ON CONFLICT DO NOTHING`; it aborts the whole batch on a duplicate key, and 38% of a DMA file is
duplicate on first ingest (ADR-0005). So the adapter COPYs into an unlogged temp table and moves
rows across with conflict handling, which keeps both the bulk-load speed and the exact insert count
the duplicate accounting depends on (ADR-0012).

**No hypertable, despite running the TimescaleDB image.**

Measured on 5,368,195 fixes, the pipeline's dominant read — `ORDER BY mmsi, ts_utc, id`, which both
the annotate pass and detection depend on — plans as:

```
Incremental Sort  (actual rows=5368195)
  Sort Key: mmsi, ts_utc, id
  Presorted Key: mmsi, ts_utc
  Full-sort Groups: 167736  Peak Memory: 27kB
  ->  Index Scan using position_report_mmsi_ts_utc_lat_lon_key
```

The UNIQUE index prefix already serves the ordering, exactly as ADR-0013 argued when it declined a
separate `(mmsi, ts_utc)` index. Only the `id` tiebreak needs sorting, in 27 kB.

A hypertable partitions by **time**. This workload's dominant access is per-**vessel** and ordered:
partitioning by time would scatter each vessel's fixes across chunks and turn one clean index scan
into a merge across all of them. The feature the image is named after would make the hot path
slower, not faster.

The image is used anyway so the extension is present the moment it earns its place — which would be
time-range queries, native compression against the 1,255 MB this table now occupies, or retention
policies. None of those is a current requirement.

## Amendments from review

Two things the three-lane review surfaced, both fixed before this record settled.

**Generated ids now follow source order.** The move from staging had no `ORDER BY`, leaving id
assignment unspecified — while `id` is the tiebreak in `ReadFixesOrdered`, so the roughly 3,700
fixes a day sharing a vessel-second could be read back in a different order on each engine. No
published figure depended on that tie, because the stop aggregates are order-independent, and
parity held empirically. But "the adapters agree" should be a guarantee rather than a coincidence
that has held so far, so the staging table carries an ordinal and the move is ordered by it.

**The batch boundary was never crossed by any test.** The default batch is 5,000 rows and the
fixture is 684, so every test called `InsertPositions` exactly once and flushed flag updates only
after the read loop finished. The staging table was therefore never recreated across transactions,
and — more seriously — the annotate pass never wrote while a read was still streaming, which is the
entire reason this adapter opens a second connection for `UpdateQualityFlags`. On a seven-day run
that path executes constantly; the suite would have stayed green while `ais detect --postgres`
failed on the only data large enough to matter. `BatchBoundaryTests` forces a 25-row batch and
exercises both.

## Consequences

- Parity is proven, not claimed. Ingesting the same seven days into both engines produces identical
  counters, and detection over them produces a **byte-identical fingerprint across all 809 stops**,
  with waiting and working totals matching to the cent.
- Postgres runs detection in 18.5 s against SQLite's 35 s, and costs more space: 1,255 MB against
  822 MB.
- SQLite remains the default. It needs no daemon, and the development loop is better for it. The
  `--postgres` flag selects the other adapter.
- `UpdateQualityFlags` opens its own connection, because Npgsql permits one active command per
  connection and the annotate pass writes while streaming a read. SQLite tolerates the
  interleaving. That difference is invisible from Core and is precisely the kind of thing running
  one suite against both engines exists to surface.
- Revisit the hypertable when the window grows past the point where a full ordered scan stops being
  reasonable, or when compression matters. Measure the same way rather than adopting it because the
  image offers it.
