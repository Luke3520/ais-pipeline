---
description: Fan out a diff to the three review lanes in parallel, then adjudicate their findings against the merge bar
argument-hint: "[branch | --staged | --working | <commit-ish> | <base>..<head>]"
allowed-tools: Read, Write, Edit, Grep, Glob, Bash, Agent
---

Run a three-lane review pass over a change, then adjudicate the results yourself.

Scope requested: **$ARGUMENTS** (empty means `branch`).

## 1. Resolve scope and capture one diff

Interpret the argument:

- `branch` or empty — `git diff main...HEAD`
- `--staged` — `git diff --cached`
- `--working` — `git diff`
- anything else — treat it as a commit-ish or range and diff that (`git show <sha>` for a single
  commit, `git diff <base>..<head>` for a range)

Write the diff to a file in your scratchpad directory, and note the base ref. **All three reviewers
must read the same file**, so capture it once rather than letting each of them run their own
`git diff`.

If the diff is empty, say so and stop. Do not spawn reviewers for nothing.

Then work out the change's **stated purpose** in one sentence, from the commit messages in range,
the PR title if there is one, or the conversation. `craft-reviewer` needs it to judge blast radius.

## 2. Fan out — all three in parallel

Spawn `qa-reviewer`, `provenance-reviewer`, and `craft-reviewer` **in a single message with three
Agent calls** so they run concurrently. Sequential spawning wastes most of the wall-clock benefit.

Give each the same briefing: the absolute path to the diff file, the base ref, the stated purpose,
and the repo root. Do not summarize the diff for them or pre-flag anything you noticed — you would
bias three independent reads into one, and their independence is the point.

## 3. Adjudicate

This is your job, not theirs. You have the build context, which is what blocking class 2 requires
and what they lack.

Work through every finding from all three reports:

- **Re-read the cited code before ruling on it.** Open the file. Memory is not evidence, and "I
  already considered that" is explicitly **not** a valid refutation. You are reviewing reviews of
  your own work, and this instruction is the only thing standing between that and rubber-stamping.
- Rule each finding **upheld**, **rejected** (with a stated reason), or **needs-investigation**.
- Dedupe: the same `file:line` with the same failure scenario from two lanes collapses into one
  finding, keeping the higher confidence and both lanes' names.
- Check each finding's `blocking_class` against the lane that raised it. A lane claiming a class
  outside its mandate is a signal the finding is miscategorized, not necessarily that it is wrong —
  reclassify it yourself.
- **A measured claim beats a reasoned one.** This project's rules were written against real data,
  and several plausible-sounding assumptions turned out to be false. If a finding and the code
  disagree about what the data does, the way to settle it is to measure, not to argue. Mark it
  `needs-investigation` and say what you would measure.

Apply the merge bar from `docs/rules/checks-and-review.md` verbatim. Only four things block: silent
loss or fabrication, damage outside the change, a broken pipeline invariant, or a crash or wrong
number on a published path. Everything else is a comment that must not hold the change.

## 4. Act

- **Upheld and blocking** — fix it now. Then run `./scripts/check.sh` and confirm it is green.
- **Upheld and non-blocking** — append it to `docs/review-followups.md` with the date, the file, the
  claim, and which lane raised it. Never drop it silently.
- **Rejected** — say so in your summary with the reason. The author deserves to see what was raised
  and dismissed, not just what survived.

**Escalate to the human rather than deciding yourself** when two lanes disagree about the same
code, when a blocking fix requires a design decision rather than a repair, when the fix would
change a calibrated threshold, or when it would touch an accepted ADR — those are superseded, never
edited.

## 5. Report

Give a short summary: how many findings per lane, how many upheld, what you fixed, what went to
follow-ups, what you rejected and why, and anything escalated. Lead with the blocking findings. If
nothing was found, say that plainly — it is a normal outcome and does not need dressing up.
