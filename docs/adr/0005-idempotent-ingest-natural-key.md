# 5. Idempotent ingest via a natural key

Date: 2026-09-11

## Status

Accepted

## Context

Message feeds re-deliver. The Danish Maritime Authority's daily files aggregate several receiving
stations, so the same AIS message appears repeatedly within a single file.

Measured on a 1,717,280-row sample of `aisdk-2026-09-05.csv`: **654,880 rows (38.2%) are exact
duplicates on `(mmsi, timestamp, latitude, longitude)` within that one file.** This is not an error
condition; it is the normal shape of the feed.

Separately, re-running an ingest — after a crash, or to pick up a corrected file — must not double
the data.

## Decision

`position_report` carries `UNIQUE (mmsi, ts_utc, lat, lon)` and ingest uses `INSERT OR IGNORE`. The
natural key is the idempotency backbone.

Duplicates are **counted, not quarantined**. At 38%, quarantining them would write millions of rows
of noise per day and bury the genuine rejects.

Latitude and longitude are included in the key deliberately. Measured on the same sample, 658,580
rows share `(mmsi, timestamp)` while only 654,880 share the full key — so ~3,700 rows are the same
vessel-second reported at *different* positions by different receivers. Those are real disagreements
and must not be silently collapsed; they are handled by rule R3 instead.

## Consequences

- Running the same file twice inserts zero rows the second time. This is the project's headline
  guarantee and is proven by an integration test, not by inspection.
- `quarantine` needs its own uniqueness constraint, or re-ingest would leave `position_report` flat
  while quarantine doubled — breaking this guarantee through the back door (ADR-0006).
- Duplicate counting must distinguish in-file from prior-run duplicates, or the guarantee is
  unreadable in the run summary (ADR-0012).
- The key relies on float equality for coordinates. This is safe because the same text parses to the
  same double, but it would break if the source were re-exported at different precision. Accepted,
  and recorded here so the assumption is visible.
- Rule R2 is therefore enforced by a database constraint rather than a pure function, so its test is
  an integration test. That asymmetry is deliberate and is stated in the README.
