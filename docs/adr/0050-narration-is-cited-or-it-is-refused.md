# 50. Narration is cited or it is refused

Date: 2026-10-06

## Status

Accepted

Not an ADR-0029 trigger: no browser, no write surface, no second user, no live feed. It is,
however, the first time anything leaves the machine, and that deserves a record of its own.

## Context

`ais log --mmsi` prints a vessel's week as cited lines (ADR-0049). They are exact and dry. A
language model can turn them into something people want to read and share: a master's log, wry
about a transponder that insists the ship is under way while it sits at anchor.

A model is also the easiest way yet for an untraceable number to reach the page. Asked to
summarise "ends a 30.4 h stop" and "stopped at 22:47", a fluent model will round to "some 30
hours", add two durations together, or produce a plausible time nobody recorded. Every one of
those reads well, and every one breaks rule 1: a number you cannot trace is a number you cannot
trust. It would be odd for a project that prints `≥42.2` rather than round a censored duration to
let a model do the rounding for it.

Asking the model nicely is not a control. The control has to be a program.

## Decision

**The model's output is validated in Core, and printed only if every sentence passes. Otherwise
none of it is printed.**

`CitedNarrative.Validate` is pure and unit-tested. It splits the reply into sentences and refuses
the whole narration if any sentence:

1. carries no `[rN·LM]` citation;
2. cites a line that was not in the evidence the model was given, even one that exists in the
   database, because the model cannot have read it; or
3. states a figure (a time, a duration, a speed, a date part) that appears in none of the entries
   that sentence cites. Arithmetic across two cited entries is refused too: the result is a
   number nobody stored. Truncating 22:02:21 to 22:02 is allowed, because it drops the seconds
   without moving the minute. Rounding 30.4 to 30 is refused, because it moves the figure.
   Citation numbers are stripped before comparing, so a citation to run 4 does not license "4".

A refused narration is never trimmed to its passing sentences. That would print a narration the
model did not write and the validator did not accept. The CLI prints each problem and then the
deterministic log. Nothing is dropped silently, and the reader always learns which log they are
reading.

**The prompt lives in Core beside the validator.** They are two halves of one contract. A rule
the prompt states but the validator does not check is only a request, and a rule the validator
checks but the prompt never stated is a trap.

**The port is thin.** `INarrator` carries a system prompt and a user message out and text back.
`ClaudeNarrator` in `AisPipeline.Adapters.Narration` implements it over the official Anthropic SDK:

- Model `claude-opus-5-5` by default, `--model` to change it. Effort `low`: rewording a dozen
  lines is not hard, and the validator, not the model's diligence, is what makes the output safe.
- `fallbacks: "default"` (beta `server-side-fallback-2026-07-01`), so a safety-classifier decline
  is re-served inside the same call on the fallback the API picks for that refusal category.
  Whatever model ends up writing, the reply passes through the same validator.
- Every API failure (401, 404, 429, 5xx, network) comes back as a sentence, not an exception, and
  the CLI then prints the plain log.

**What leaves the machine:** one vessel's name and its log lines, which are derived from the
public DMA feed, and the fixed prompt. Never a Statement of Facts, charter party terms, a file
path, or anything else a user supplied. The request is made only on `--narrate` with a credential
present. Without one, the CLI says so and prints the plain log.

## Consequences

- The validator cannot catch a false claim made only of words. "She was waiting for orders" cites
  fine and contains no figure. The prompt forbids motive, and the CLI's closing line says what was
  checked and what was not. A validator that claimed to catch everything would be a bigger lie
  than the one it stops.
- Narration is non-deterministic, so it is never stored, exported or used by anything downstream.
  It is printed and gone. Derived tables remain projections of `position_report` (rule 5), and
  nothing reads prose back.
- The tests cover the validator and the prompt in Core, with no network. The adapter was exercised
  against the live API only as far as a deliberately invalid key, which returned 401 and printed
  the fallback correctly. **A successful narration had not been run when this was accepted**,
  because there was no API key in the environment it was built in. The first person to run
  `--narrate` with a key is the first end-to-end test. If the validator refuses most real replies,
  that is a prompt problem, to be fixed in `NarrationPrompt` without loosening `CitedNarrative`.
- The validator's strictness is the feature. If a future change makes it more permissive (accepting
  rounding, say), that is a reversal of this record and needs a new one.
