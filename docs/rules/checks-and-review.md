---
paths: .github/workflows/**, scripts/**, Directory.Build.props, .claude/**
---

# Checks and review

## What blocks a merge

Only four kinds of finding block a change:

1. **Silent loss or fabrication.** A row dropped, invented, or altered without a `quarantine`
   record, a `quality_flags` entry, or a counter in `ingest_run`. This is the project's signature
   defect: the pipeline exists to be trustworthy about what it kept and what it refused.
2. **Damage outside the change.** A bug introduced in code the change did not set out to touch.
3. **A broken pipeline invariant.** Idempotency (same file twice inserts zero), provenance (every
   record resolves to an ingest run and source line), or a rule's declared reject/flag contract.
4. **A crash, or a wrong number on a path the project publishes** — a CLI verb, the quality report,
   or a figure quoted in the README.

Everything else — style, naming, "I would have done it differently", missing abstractions that
would serve one call site — is a comment the author may act on or file as a follow-up. Do not hold
a change for it.

## Why these four and not security

There is no security lane yet because there is no attack surface: the pipeline is a local CLI
reading public AIS files, with no auth, no tenants, no user data and no network listener. A lane
with nothing to review either sits silent or manufactures findings, and the second is worse than
not having it.

That changes at **M4** (HTTP API) and **M7** (charter-party and Statement of Facts data, which is
commercially sensitive). A security lane is added then, with an ADR, and the blocking classes are
renumbered in a superseding record. See ADR-0022.

## Adding a blocking check

`./scripts/check.sh` runs format, build with warnings-as-errors, and tests. It runs pre-push and in
CI on every push and pull request.

A new blocking check is admitted only for a defect that actually happened, or was caught by luck.
Name the defect in a comment where the check is configured. This keeps the gate small and every
rule in it justified.

Two checks were admitted up front rather than after a failure, and both are recorded here so the
exception is visible:

- **`TreatWarningsAsErrors`** in `Directory.Build.props`. Nullable-reference warnings are the
  cheapest available signal about the null handling this domain is full of.
- **The `AisPipeline.Tests` → `Core`-only project reference.** It makes the no-I/O-in-Core rule
  (ADR-0003) a compile error rather than a convention, and that rule is what keeps every quality
  rule unit-testable.

## Zero tests must not pass as green

`dotnet test` exits 0 when it discovers no tests, so a change that breaks test discovery looks
identical to a change where everything passed. `RunConfiguration.TreatNoTestsAsError=true` closes
that, and `scripts/check.sh` sets it on the integration suite.

Both suites carry it. The unit suite was exempt while `AisPipeline.Core` had no types and zero
unit tests was the correct state; that exemption ended when M1 added the first quality rules, and
the flag went on in the same change.

## Running against both adapters

`./scripts/check.sh` runs the integration suite against SQLite always, and against Postgres when
`AIS_POSTGRES` is set:

```bash
docker compose up -d
export AIS_POSTGRES="Host=localhost;Port=55432;Database=ais;Username=ais;Password=ais"
```

Skipping Postgres locally is a deliberate convenience for anyone without Docker. It is also how
adapter parity quietly stops being tested, so `AdapterCoverageTests` **fails in CI** when the
variable is unset there. If that test fires, fix the workflow's service container rather than the
assertion (ADR-0026).

## Enabling the hook

The hook lives in `.githooks/pre-push` and is version controlled, but git does not read it until
the clone is pointed at that directory:

```bash
git config core.hooksPath .githooks
```

This is local config and a clone does not inherit it. Until it is run, the pre-push check silently
does not fire -- indistinguishable from a check that passed. The README says the same thing in its
setup section.

## Skipping the hook

`git push --no-verify` is fine for work-in-progress branches that will not be merged as they are.
Never for `main`.
