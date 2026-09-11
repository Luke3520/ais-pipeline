# 27. REST for operations, GraphQL for analysis, and proving the N+1 is gone

Date: 2026-09-11

## Status

Accepted

## Context

The pipeline's output is a graph of nested aggregates — vessel → port calls → phases → stops →
fixes — and different clients want very different slices of it. A map wants positions; a laytime
calculation wants waiting and working hours; a data-quality review wants what was refused and why.

Over REST that is either over-fetching or a slow growth of ad-hoc `include` parameters. Over
GraphQL it is one query per client. But GraphQL's default execution shape is a resolver per field
per object, which turns a page of 50 port calls into 50 vessel lookups.

## Decision

**REST is the operational surface; GraphQL is the analytical one, and they do not duplicate each
other.** `/health`, `/vessels`, `/stops`, `/portcalls`, `/quality`, `/runs` are resource-shaped,
stable and cacheable, described by a generated OpenAPI document. Anything deeply nested or
selective goes to `/graphql`. Building both over the same shapes would be work with no reader.

**The N+1 is closed with DataLoader and proved by counting queries, not by timing them.**

Counting is the only honest assertion available. A GraphQL response looks identical whether it took
one round trip or a hundred, and a hundred queries against a warm local SQLite file is still
milliseconds — so wall-clock timing over a fixture cannot distinguish them. Having DataLoader wired
up is not evidence that it is used; the count is.

Measured against the seven-day database, the fully nested query — 50 vessels, their port calls,
their phases, their stops — returns 50 vessels, 60 port calls and 161 phases in **3 queries**.
Unbatched that is `1 + 50 + 60 = 111` round trips. Warm latency is ~10 ms.

`GraphQLNPlusOneTests` asserts the count directly, and asserts the property that actually
distinguishes batched from unbatched: **query count does not grow with page size.**

**Execution depth is capped at 10.** The nesting is the point of the surface, so depth has to be
allowed — but each level multiplies work, and unbounded depth is a denial of service. Limits on
every collection endpoint are clamped server-side for the same reason; an unbounded `LIMIT` taken
from a query string is a denial of service with extra steps.

**One read implementation serves both engines, with a single named dialect knob** — a deliberate
contrast with ADR-0026, which writes the *schema* out twice. DDL genuinely differs per engine;
plain SELECTs mostly do not. "Mostly" was doing real work, and building it this way found the three
places, none of which would have failed on one engine alone:

1. **Dapper binds positional records through the constructor**, which does no type conversion, so
   SQLite's TEXT timestamps would not materialise. Read models bind by property instead, with an
   explicit UTC type handler — `Convert`'s string-to-`DateTime` path yields `Kind=Local` and would
   have shifted every instant by the machine's offset **without throwing**.
2. **Dapper's SQLite parameter binding is case-sensitive.** `new { filter.Mmsi }` binds `@Mmsi` and
   leaves the `@mmsi` in the SQL unbound.
3. **Booleans are semantics, not syntax.** SQLite has no boolean type and stores 0/1; Postgres
   rejects `is_complete = 1` outright. Dapper cannot smooth that over the way it smooths over
   parameter naming. Batched lookups likewise use `= ANY` on Postgres, which Npgsql survives where
   Dapper's `IN` expansion does not — and which keeps one prepared plan whatever the batch size,
   the shape DataLoader calls with a different count every time.

## Consequences

- The batched port methods (`GetVessels`, `GetPortCallsForVessels`, `GetPhasesForPortCalls`) exist
  because a resolver cannot batch what the port cannot express. They are tested to agree with the
  single-row path, so fixing the N+1 changes timings and not answers.
- No authentication, deliberately. The data is public AIS and there is nothing to protect; auth
  arrives with charter party and Statement of Facts data, which is commercially sensitive
  (ADR-0017). Deferring it with a stated reason beats bolting it onto a public dataset.
- **The fixture did not contain the project's headline finding.** Writing the GraphQL test for rule
  R10 revealed that not one stop in the committed fixture had `status_agrees = 0`, so the 63.8%
  disagreement the README leads with had no end-to-end coverage at all. A tanker that sits still
  for 159 minutes while reporting "Under way using engine" is now in the fixture, and
  `FixtureIntegrityTests` pins it.
- The query-counting harness has to tally across DI scopes. HotChocolate resolves the query root
  and the DataLoaders from different scopes, so counting a single instance measures part of a
  request and reports a smaller, plausible number.
