# 32. The quality report counts both halves

Date: 2026-09-11

## Status

Accepted

## Context

Rule 2 of CLAUDE.md gives a rule exactly two options: *reject* a row, which writes a `quarantine`
row carrying the rule id and the raw text, or *flag* it, which keeps the row and records the doubt
in `position_report.quality_flags`. There is no third option.

`QualityReport()` counted one of them:

```sql
SELECT rule_id AS RuleId, COUNT(*) AS Quarantined
FROM quarantine
GROUP BY rule_id
```

Every rule that flags rather than rejects — R7 teleport, R8 coverage gap, R11 speed consistency —
was therefore absent from the report entirely. Not zero: absent. These are the three rules the
annotate pass exists to run, the three ADR-0021 orders *before* detection because detection
excludes flagged fixes from centroid and drift, and the three whose output ADR-0025 uses to decide
whether a stop's geometry can be trusted at all. The report that is supposed to answer "what was
wrong with this data" showed none of them.

The effect is worse than an omission. A reader who runs the report sees R1, R4, R5, R6 and
concludes those are the problems in the feed. The rules that annotated millions of fixes with
recorded doubt are invisible, and the doubt reads as absence of doubt — a number you cannot trust,
which is the one thing this project exists to prevent.

A second gap sat behind it: the report was built from whatever ids happened to be in the data, so a
registered rule that never fired did not appear either. R6 has never fired on the seven-day window.
That is a finding about the feed. Silence and absence are not the same claim, and only one of them
is true.

## Decision

**`RuleHitCount` carries `Quarantined` and `Flagged` separately, and a `Total`.** Separately rather
than summed, because the two mean different things to the reader: a quarantined row is not in the
time series and a flagged row is. Collapsing them would answer "how often did this rule fire" while
destroying "what happened to the data".

**The report is built against the rule registry, not against the data.** `QualityReport.Build` in
Core takes the registry and the counts and reconciles them. A registered rule with no counts prints
zero and is marked silent. A count whose id is in no registry prints too, labelled — rows in the
store carrying an id nothing runs means a rule was retired or renamed without migrating its rows,
and that is precisely the case worth seeing.

**The sequence rules move into `RuleRegistry`.** They were hand-listed at the CLI call site, so the
registry did not know about the rules the annotate pass ran, and a report built from the registry
would have reported truthful zeros for checks it had no idea were performed. `AnnotatePass` now
takes `RuleRegistry.Default().SequenceRules`, which makes the list the report enumerates and the
list the pass applies the same object.

**Flagged counts come from decomposing the flag string, in one scan.** `quality_flags` is a
comma-separated column, not a table, so there is no `rule_id` to group by. The query groups by the
whole string and splits it in the adapter. Distinct flag combinations are bounded by the number of
rules, not by the number of rows, so the grouped result is a handful of rows however large the
table is. The obvious alternative — a `LIKE '%,R7,%'` predicate per rule — is one full scan per
rule and gets slower every time a rule is added.

## Consequences

`ais quality` exists, which CLAUDE.md's definition of done has required since it was written ("a
new or changed quality rule has its counter visible in `ais quality`") and the README has documented
since before the CLI had verbs. Until now there was no way to satisfy that clause.

Normalising flags into their own table was not done. It would make the query a plain `GROUP BY` and
would let a flag carry its own provenance, but it doubles the write cost on the hot ingest path for
a read that runs once per investigation. Revisit if flags ever need per-flag detail rather than a
count — the shape of the read model would not change.

The report says nothing about *which* rows a rule flagged. `ais quality` is a census, not an
investigation; the rows are in `position_report` with their ids intact and `quarantine` keeps the
raw text. Following a count down to its rows is a verb that does not exist yet.
