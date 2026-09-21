# 46. An archive nothing reads is not an archive

Date: 2026-09-21

## Status

Accepted

Extends [ADR-0045](0045-prune-whole-periods-archive-first.md), which decided prune must archive
before deleting. It did not say what makes an archive real.

## Context

ADR-0045 shipped the archive and verified it like this:

```csharp
if (!File.Exists(archivePath) || new FileInfo(archivePath).Length == 0)
```

Non-empty. That is the whole of it, and it sits in front of a delete that removes the only rows the
file describes.

Three things were wrong with the state it left behind, and they compound:

**Nothing could read the file.** The document was assembled inline as an anonymous type in the CLI
and serialised on the spot. No type described its shape, no code anywhere deserialised it, and no
test asserted anything about it. The claim "the history is preserved" rested on a writer that had
never been read.

**Non-empty is not readable.** A prune interrupted between opening the file and flushing it leaves
valid-looking JSON that simply stops — the exact failure this check passes. The archive would be
declared good, 3.4 million fixes would be deleted, and the loss would be discovered by whoever next
went looking for a call that no longer exists anywhere.

**The file outlives the code that wrote it.** That is its entire purpose. It carried no version, so
a reader meeting one years later has no way to ask what shape it is, and a changed writer would
deserialise silently into whatever still fits.

This is the project's own rule 1 turned on the one artefact that most needs it. A number you cannot
trace is a number you cannot trust; a record you have never read back is a record you have no
grounds to call preserved.

## Decision

**The archive has a declared shape, in Core.** `ArchiveDocument` and its parts sit in
`Core/Archive` beside the export model, every property `required`, so a document missing a field is
a deserialisation failure rather than an `mmsi` that reads back as `0`. The builder is a pure
function over what the store still knows, unit-tested in the Core suite like every other pure thing
here.

**One pair of functions writes and reads it.** `ArchiveJson` in `Adapters.Archive` — reading and
writing in the same place, over the same types, so a writer and a reader cannot drift apart. This
follows `SofJsonReader` (ADR-0031): a document format is an adapter's business, and Core holds only
its shape.

**Prune reads the archive back before it deletes anything.** Parse it, count the calls, compare
against what is about to be lost, and refuse the prune on any mismatch. The verification a future
reader will perform is the only verification worth doing, so prune performs exactly it. This is the
change with teeth: prune is now fail-closed on an archive it cannot read.

**That order is a domain rule, so it lives in Core behind a port.** `PrunePass` takes an
`IPortCallArchive` and an `IAisStore` and decides between them; `JsonFileArchive` supplies JSON and
a directory. The first version of this rule sat in the CLI, where no test project reaches — and a
rule whose only exercise is deleting three million rows off a real disk is a rule nobody runs. It
is now asserted with a fake archive that refuses to read: the store is never asked to prune. That
is the whole claim, and it needed a seam to be a claim at all.

**`ais archive <file>` reads one.** Past the cutoff this file is the only record that a call
happened, and a record that needs a program written before anyone can look at it is a record in
name only. With `--mmsi` it prints the phases too, which is the question that makes someone open a
pruned record at all.

**A `formatVersion`, refused by name when it is not ours.** Version 1 today. A reader that meets
version 2 says so and stops, rather than deserialising a changed shape into whatever still fits and
reporting the result as history.

**Absent means version 0, and version 0 still reads.** It is the one field on the document that is
not `required`. An archive written by ADR-0045's prune has no version, and refusing it would have
left the first real archives this code inherits unreadable by the tool built to prove archives are
readable — on files whose underlying rows are, by construction, already deleted. This is not a
guess about their shape: the pre-versioning writer emitted the same field names in the same camel
case for every value the document holds, so version 0 is this format minus this field, and a test
reads one written exactly as that code wrote it. Refusal is forward-only: an unknown *higher*
version is a changed shape and still stops.

**Three states are refused rather than tolerated**, all of them loudly and with the file named:
unparseable, wrong version, and carrying no calls. The last matters because an archive with no port
calls is not a record of nothing — it is a record that failed to be written, and prune declines to
delete on the strength of one.

## Consequences

**Prune can now fail where it used to succeed.** An archive that will not read back stops the
delete. That is the point, and it is the right direction for the trade: a prune that does not
happen costs disk, and a prune that happens against a broken archive costs the history.

**The round trip is asserted as bytes, not field by field.** Write, read, write again, and the two
renderings must be identical. A field added to the document later and forgotten by either side
changes the second rendering, where an assertion naming today's fields would keep passing over the
gap. The test has to cover fields nobody has thought of yet.

**Reading is laxer than writing**, deliberately: comments skipped, trailing commas allowed, property
names case-insensitive. The file is meant to be opened, and someone who reindents one or annotates
it with a note about why a call mattered should not find it unreadable afterwards.

**The archive is still JSON, not SQL.** ADR-0045 accepted that and nothing here changes it — no
joins, no benchmarks, no API over pruned calls. What changed is that the JSON is now readable on
purpose rather than by luck.

**Verified against the real store, not the fixture**, which is the standard ADR-0045 set after the
fixture missed four failures: 235 calls archived to 292 KB and read back, 3,448,821 fixes removed,
`detect` rebuilding from what remained, and the three refusals — truncated, wrong version, missing
— each exiting non-zero with the file named. Run again after the logic moved into Core, and the
figures did not move.

**A prune with no old port calls writes no archive at all**, and says so in `retention_event`
rather than leaving the path column implying a file that does not exist. Refusing an empty document
on read and writing one on an empty prune are the same decision seen from two sides: the first
caught a failed write, and without the second it would have blocked a prune that had nothing to
lose. Fixes with no derived call behind them are still fixes, and still past the time bar.

**A review lane caught this one.** The version check was written as equality against the current
version, which would have refused every archive written before the check existed. No such file
survives on the machine this was built on — searched, and the ADR-0045 measurement run's archive
was not kept — so nothing was actually lost. It was still the change's own promise failing on the
only archives it did not write, and equality was one character away from being a one-way door.

**`sourceFiles` lists the whole store's files, not only the pruned days.** A run records how many
rows it read, not the window it covered, so narrowing the list would be a guess dressed as
provenance. The field says so rather than implying otherwise.
