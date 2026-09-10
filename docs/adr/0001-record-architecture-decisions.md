# 1. Record architecture decisions

Date: 2026-09-11

## Status

Accepted

## Context

This project's central claim is that a number you cannot trace is a number you cannot trust. Every
normalized record points back to the source file and line it came from; every quality rule has an
id, a test, and a stated action.

The design itself deserves the same treatment. Decisions made in conversation are lost within weeks,
and a reader arriving at the repository later — including the author — cannot distinguish a
deliberate choice from an accident. Several decisions here are counter-intuitive and rest on measured
evidence rather than convention; without a record they read as arbitrary.

## Decision

Record architecturally significant decisions as numbered Markdown files in `docs/adr/`, using
Michael Nygard's format. Records are immutable once accepted and are superseded rather than edited
or deleted. Decisions that rest on measurement carry the measurement and the method.

Rejected alternatives are recorded with their reasoning, not silently dropped.

## Consequences

- The reasoning behind the design survives independently of the person who made it.
- Reversals become visible as history rather than invisible as edits.
- Writing a record is a small tax on each decision, which is intended: it discourages decisions made
  without a reason worth writing down.
- The repository gains a second, parallel provenance trail — one for the data, one for the design.
