# Review follow-ups

Non-blocking findings from `/review-pass`, kept so they are not lost. Nothing here holds a change —
see `docs/rules/checks-and-review.md` for what does.

Append newest first. Delete an entry when it is fixed or consciously declined.

| Date | File | Lane | Finding |
|---|---|---|---|
| 2026-09-11 | `src/AisPipeline.Core/Detection/StopDetector.cs` | (self, M2) | A berthed vessel briefly exceeding 1.0 kn closes its stop, so one berth stay can split into many. Investigated: only ~4% of splits are spurious (gaps under 5 min where the vessel stayed within 90 m). Gaps of 5-60 min involve real movement of 0.3-2.7 nm and are genuine shifts. A debounce would fix 20 of 447 splits and was **not** implemented -- waiting and working hours are barely affected (mean 0.49 h per call unaccounted). Revisit only if laytime needs finer granularity. |
| 2026-09-11 | `src/AisPipeline.Cli/Program.cs` | craft | The CLI hand-rolls verb dispatch and flag parsing (`switch (args[0])`, a `ValueOf` scanner). Fine for one verb; revisit when M2 adds `detect` and a third verb makes the duplication real. The unused `System.CommandLine` prerelease reference was removed rather than adopted, since adopting a parser library for a single verb would itself be over-building. |
