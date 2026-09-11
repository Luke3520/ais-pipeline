# 17. Defer authentication until there is something to protect

Date: 2026-09-11 (decision made during M4; recorded when a citation check found it missing)

## Status

Accepted

## Context

The API serves AIS data: vessel positions, stops, port calls, and what the pipeline refused. All of
it derives from the Danish Maritime Authority's open feed, which is free to download by anyone.

The reflex is to add authentication because an API without it looks unfinished. That reflex is
worth resisting here, and the reason matters: an auth layer over public data protects nothing while
adding a login flow, a token lifecycle, and a credential store — three things that can leak, expire
wrongly, or be misconfigured. It is machinery with a cost and no corresponding risk.

There is a specific, foreseeable point where that changes.

## Decision

**No authentication while the data is public.** The API is read-only, every endpoint derives from
the open feed, and there is nothing an attacker could obtain that they could not download directly
from the source.

**Authentication becomes mandatory the moment charter party terms or Statement of Facts data
arrives** (M6–M7). Those are commercially sensitive in a way AIS is not: a demurrage rate, an
agreed laytime allowance, and a signed timeline of what the parties say happened are the terms of a
private contract between a charterer and an owner. They are not ours to expose, and a leak is a
commercial disclosure rather than an inconvenience.

At that point: an OIDC provider (Keycloak in Compose, or Azure Entra), JWT bearer validation, and
scopes separating read from ingest-trigger from fixture management.

Until then, the surface is defended by what actually applies to a public read API: execution depth
capped at 10, every collection limit clamped server-side, and no write path at all (ADR-0027).

## Consequences

- The README says the API is unauthenticated and why, rather than leaving a reader to assume it was
  forgotten. Deferring with a stated reason is a different thing from omitting.
- **This is a trigger, not a preference.** ADR-0029 lists a write surface as one of the conditions
  that re-opens the architecture, and this is the concrete reason: the first upload endpoint changes
  the threat model from "nothing to protect" to "a contract between two companies".
- Adding auth later touches the API only. The pipeline, the store ports and the domain are
  unaffected, which is what makes deferring it cheap rather than a debt.
- Recorded late. This decision was made and cited twice during M4 before it was written down, and
  only surfaced when `scripts/check-adr-citations.sh` refused a citation that resolved to nothing.
  A decision relied upon in two other records is not an implicit one.
