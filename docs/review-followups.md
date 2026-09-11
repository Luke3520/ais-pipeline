# Review follow-ups

Non-blocking findings from `/review-pass`, kept so they are not lost. Nothing here holds a change —
see `docs/rules/checks-and-review.md` for what does.

Append newest first. Delete an entry when it is fixed or consciously declined.

| Date | File | Lane | Finding |
|---|---|---|---|
| 2026-09-11 | `src/AisPipeline.Cli/Program.cs` | craft | The CLI hand-rolls verb dispatch and flag parsing (`switch (args[0])`, a `ValueOf` scanner). Fine for one verb; revisit when M2 adds `detect` and a third verb makes the duplication real. The unused `System.CommandLine` prerelease reference was removed rather than adopted, since adopting a parser library for a single verb would itself be over-building. |
