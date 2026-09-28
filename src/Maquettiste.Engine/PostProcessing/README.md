# PostProcessing

**Owner:** W8 Formatters and schema diff. See docs/engineering/engine-design.md section 18.

Line endings, protected regions and formatters (stage 7; engine-design.md section 13). Implemented.

- `PostProcessor` (`IPostProcessor`): per rendered file, in the design's order: (1) CRLF and CR to LF, BOM stripped, UTF-8;
  (2) format with the unit's formatter (by name; by longest matching extension when unnamed, first configured wins a tie;
  `"none"` disables it; an unknown name is MQ6014, a warning, once per pack unit per run, pointing at
  `<pack folder>/pack.json` `/units/<i>/formatter` with no element or output path, so it does not depend on which worker
  processed the unit first); (3) in `regions` mode, keep every
  `maquettiste:keep id=<id>` … `maquettiste:end-keep` body from the file on disk; (4) hash and classify the root with
  `IOutputPathPolicy.Check`. Nothing is written, so dry runs and `--check` use the same code in memory.
  Manifest hashes: `r:` + skeleton hash for regions files, `o:` + content hash for owned files (`once` mode and pair
  companions), else the content hash. A `pair` unit's file blocks get mode `overwrite`; every other file keeps the unit's mode.
- `FormatterRunner` (`IFormatterRunner`): runs `Command` with `ArgumentList` (no shell; `{path}` replaced by the repo-relative
  path) in the repo root, input on stdin, output from stdout, stderr captured for the message. Non-zero exit, timeout
  (`TimeoutSeconds`, process tree killed), a command that cannot start, or output that is not UTF-8 is MQ6008.
  `VerifyVersionsAsync` runs `Command VersionArgs` and requires the output (stdout and stderr) to contain the pinned `Version`
  (ordinal); diagnostics point at `maquettiste.json` `/formatters/<i>`. Cancellation kills the process and throws.
- `ProtectedRegions`: parser, merge and skeleton. Duplicate ids, a start inside an open region, an `end-keep` without a start,
  a `keep` without `id=` and an unterminated region are errors.
- `TextNormalizer`: LF, no BOM, strict UTF-8 (`TryEncode` and `TryDecode` report invalid text instead of throwing).

Failure mapping: a failed unit returns no files (the writer keeps its previous outputs) and carries the diagnostics.
MQ6004 path refused; MQ6015 regions on a built root; MQ6006 malformed region markers in the generated output (a template bug)
or rendered text that is not valid Unicode (an unpaired UTF-16 surrogate, for example a string sliced inside an emoji; the file
never reaches the formatter);
MQ6010 a region on disk the new output lacks, malformed markers on disk, or a disk file that is not UTF-8; MQ6008 formatter.

Deviations and notes:

- Formatting runs over rendered units only (skipped units never reach stage 7), which is how "changed files only" is met.
- When `PostProcessContext.FormatterVersionsVerified` is false, each formatter's version is checked once per run (per
  context instance, held in a `ConditionalWeakTable`, so nothing outlives the run) before its first use; a mismatch fails the
  units that would use it. The orchestrator is still expected to call `VerifyVersionsAsync` up front and fail the run.
- A formatter named by a unit applies to every file of that unit, whatever its extension.
- The writer (W7) must hash a regions file on disk with `ProtectedRegions.SkeletonHash(normalized text)` to compare it with its
  `r:` manifest entry.

Tests: `tests/Maquettiste.Engine.Tests/PostProcessing/`. Formatter tests run a small Python fake formatter written by the test
(skipped only when no Python is on `PATH`) and `dotnet --list-runtimes` for version pinning; never Docker or network.
