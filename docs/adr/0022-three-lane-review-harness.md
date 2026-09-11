# 22. Three-lane review harness

Date: 2026-09-11

## Status

Accepted

## Context

Most of this project's code is written by an AI agent, and the same agent then judges whether the
work is sound. That is a conflict of interest, and the failure mode is not laziness but
plausibility: a change that reads correctly, passes its tests, and quietly loses data.

Three defects of exactly that shape were found during planning and M0, none by reading the code:

- A per-row `Ship type == "Tanker"` filter silently dropped 2.6% of each tanker's own fixes.
- One teleport fix poisoned a stop's centroid, producing 2,387 nm of drift over hundreds of good
  fixes.
- A `*.csv` gitignore pattern matched the *directory* `AisPipeline.Adapters.Csv` on a
  case-insensitive filesystem, silently untracking an entire project until CI failed on a fresh
  clone.

All three looked right. Each was caught by measurement or by an external check, not by review.

A single general-purpose "review this" pass does not help, because an unbounded reviewer with an
unbounded subject can always find something, so its reports become noise and stop being read.

## Decision

Adopt a three-lane review harness, adapted from the pattern used in a sibling project.

`/review-pass` captures **one** diff, spawns three read-only lanes concurrently over it, then
adjudicates. Each lane owns specific blocking classes and is explicitly told what is *not* its
lane:

| Lane | Owns | Question it answers |
|---|---|---|
| `qa-reviewer` | class 4 | Does this behave correctly across its input space, and do tests prove it? |
| `provenance-reviewer` | classes 1, 3 | Can every row kept, refused or derived be accounted for? |
| `craft-reviewer` | class 2 | Is this the smallest correct version of itself, and does it match its neighbours? |

The merge bar in `docs/rules/checks-and-review.md` admits only four blocking classes. Everything
else is a comment recorded in `docs/review-followups.md`, which never holds a change.

Three disciplines make the lanes usable rather than noisy, and they are stated in every agent:
**zero findings is a successful review**; every finding must be falsifiable as a specific input
producing a specific wrong outcome; and at most five findings, ranked.

**`provenance-reviewer` replaces the security lane** the source pattern used. This pipeline is a
local CLI over public AIS files — no auth, no tenants, no user data, no listener — so a security
lane would either sit silent or manufacture findings. The real adversary here is a change that
loses or invents rows, which is the same shape of question (trace a path from input to a bad
outcome) pointed at the risk this project actually has.

## Consequences

- Three independent reads cost three concurrent agents per review. Capturing the diff once rather
  than per-lane keeps them reviewing the same thing and keeps the cost bounded.
- The adjudicator is instructed to **re-read every cited line** before ruling, and that "I already
  considered that" is not a valid refutation. Without that, the agent reviewing its own work
  rubber-stamps.
- Escalation to the human is required when lanes disagree, when a fix needs a design decision, when
  it would change a calibrated threshold, or when it would touch an accepted ADR.
- A security lane is added at **M4** (HTTP API) and **M7** (charter-party and Statement of Facts
  data, which is commercially sensitive). That will renumber the blocking classes, and it needs a
  superseding ADR rather than an edit to this one.
- The lanes depend on `CLAUDE.md` and `docs/rules/` staying accurate. A rule that drifts from the
  code turns three reviewers into three sources of false findings, so rule files are updated in the
  same change as the behaviour they describe.
