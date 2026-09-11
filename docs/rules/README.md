# Rules

One file per rule that is too long for `CLAUDE.md`. Each file opens with a `paths:` line naming the
files it governs, so anyone touching those files knows to read it.

| Rule | Governs |
|---|---|
| [checks-and-review.md](checks-and-review.md) | What blocks a merge, when a check may become blocking |
| [units-and-geodesy.md](units-and-geodesy.md) | Distances, speeds, times, parsing, rounding |
| [quality-rules.md](quality-rules.md) | Adding or changing a quality rule |
| [detection.md](detection.md) | Stop detection, port-call chaining, thresholds |
