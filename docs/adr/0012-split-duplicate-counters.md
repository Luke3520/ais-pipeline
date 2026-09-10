# 12. Split duplicate counters

Date: 2026-09-11

## Status

Accepted

## Context

The headline guarantee is that ingesting the same file twice changes nothing. The natural way to show
that is a run summary reporting rows read, inserted and duplicate.

But 38.2% of any single DMA file is already duplicate on first ingest, because the feed merges
several receiving stations (ADR-0005). A single `rows_duplicate` counter therefore reads ~38% on the
*first* run and ~100% on the second, and a reader cannot tell which part of that number demonstrates
idempotency and which part is just the shape of the feed.

## Decision

`ingest_run` carries two counters:

- `rows_dup_in_file` — the natural key was already seen earlier in *this* file. Receiver duplication.
- `rows_dup_prior_run` — the natural key was already in the store before this run began. Re-delivery
  or re-ingest.

`rows_filtered` is likewise counted rather than silent, so rows excluded by the tanker scope guard
are visible instead of vanishing.

## Consequences

- The idempotency proof is legible: a second ingest of the same file shows `rows_inserted 0` with
  every duplicate attributed to `rows_dup_prior_run`.
- Distinguishing them requires knowing whether a key existed before the run started, so the ingest
  keeps an in-run seen-set rather than relying solely on the database's conflict count.
- The README can quote a real run summary where each number means one thing.
