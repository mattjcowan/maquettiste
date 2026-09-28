# Maquettiste.Cli

**Owner:** W9 CLI. See docs/engineering/engine-design.md sections 16 and 18.

The `maquettiste` dotnet tool: a hand-written parser (D27), `init`, `validate`, `generate`, `migrate` (stub), `pack new`, `bench`,
console progress and exit codes. Consumes `ModelStore`, `GenerationService`, the internal `GenerationWatcher` (watch hook),
`SarifWriter`, `SchemaRegistry`/`CanonicalJson` (init, pack new), `OutputPathPolicy` (every CLI write) and `BenchmarkHarness`.
Tests: `tests/Maquettiste.Cli.Tests/` (in-process runs over temporary repos built from `tests/fixtures/models/billing`, plus one
test that runs the built tool as a process).

## Install

The package is a .NET tool (`PackAsTool`, command `maquettiste`). Pin it per repo with a tool manifest,
`.config/dotnet-tools.json`:

```json
{
  "version": 1,
  "isRoot": true,
  "tools": {
    "maquettiste.cli": {
      "version": "1.0.0-alpha.1",
      "commands": ["maquettiste"],
      "rollForward": false
    }
  }
}
```

then `dotnet tool restore` and `dotnet maquettiste generate` (or `dotnet tool install --global Maquettiste.Cli` for `maquettiste`).
Build a local package with `dotnet pack src/Maquettiste.Cli -c Release -o artifacts`.

## Files

| File | Holds |
| --- | --- |
| `Program.cs` | `Main`, `ExitCodes`, Ctrl+C → cancellation |
| `CliEnvironment.cs` | the process view a command gets (stdout, stderr, current directory, environment); tests inject string writers |
| `CliApp.cs` | dispatch, usage text, global options (`GlobalContext`: repo and cache folder resolution, engine options) |
| `CommandLine.cs` | the parser: `--name value`, `--name=value`, flags, `-h -q -j -p` aliases, `--`; per-command option checks |
| `ConsoleProgress.cs` | `IProgress<ProgressUpdate>` on stderr: terminal, plain, json, none |
| `DiagnosticOutput.cs` | diagnostic text lines and `diagnostics.json` objects |
| `GuardedFiles.cs` | every file or folder the CLI writes goes through `IOutputPathPolicy.CheckEngineWrite` first |
| `StarterPacks.cs` | the embedded example packs (`Maquettiste.Cli.Packs/<pack>/<path>`) and built-in stand-ins |
| `Commands/*.cs` | one class per command |

## Behavior

- **Globals:** `--repo` (default: the nearest ancestor of the current directory holding `.maquettiste/maquettiste.json`, else the
  current directory; `init` never searches), `--cache-dir` (default `$MAQUETTISTE_CACHE_DIR`, else `~/.cache` (or
  `$XDG_CACHE_HOME`, `~/Library/Caches` on macOS, `%LOCALAPPDATA%` on Windows) `/maquettiste/<first 16 hex of SHA-256 of the repo
  path>`), `--jobs`, `--progress auto|plain|json|none`, `--verbosity quiet|normal|detailed`, `--quiet`/`-q`, `--no-color`
  (accepted; the CLI prints no colors), `--version`, `--help`. Options may appear anywhere after the program name.
- **Streams:** stdout carries results only (plan lists, diffs, JSON, SARIF, `pack new` paths, the `migrate` message); progress,
  summaries, diagnostics of `generate` and errors go to stderr.
- **Exit codes:** 0, 1 (validation, pack, template or script errors), 2 (drift), 3 (hand edits and region conflicts), 4 (usage
  error, internal error, busy lock, cancellation, refused write). The engine's `RunOutcome` already applies the precedence 4, 1, 3,
  2 (`Generation/Outcomes.cs`, which also maps MQ6010 to conflicts); the CLI maps outcomes one to one.
- **init:** creates the phase 1 folders under `.maquettiste/` (`model/{packages,entities,relations,enums,types,databases,mappings,
  diagrams,vocabularies}`, `templates`, `extensions`; a `.gitkeep` in each folder still empty), a canonical `maquettiste.json`
  (format 1, `outputs.allow` `db` committed and `src/Generated` built, `packs.<starter>.output` `db` for sql-ddl or
  `src/Generated` for csharp-dapper), every embedded schema in `.schema/v1/` (refreshed on every run; files no longer shipped are
  removed), the starter pack in `templates/<pack>/`, and a `# maquettiste:begin` … `# maquettiste:end` block in `.gitignore` with
  the built roots of the settings file and `/.maquettiste/.cache/` (rewritten in place, other lines kept). Existing files are kept,
  except that a kept settings file without a `packs.<starter>` entry gets one (re-running `init --pack <other>`). A `.gitignore`
  whose markers are not exactly one begin line followed by one end line is refused with the line number (exit 4) before anything
  is written, so stray markers never cause user lines to be dropped.
  `--hooks` writes `post-checkout` and `post-merge` hooks (mode 755) that run `maquettiste generate --roots built --quiet` and never
  fail git; a hook that init did not write is kept; without a `.git` folder the hooks are skipped with a message.
- **validate:** model diagnostics (`ModelStore.ValidateAsync`, load diagnostics included) plus pack-load diagnostics, sorted;
  `--format text` (one `path(line,col): severity rule: message` line per diagnostic), `json` (`schemas/v1/diagnostics.json`) or
  `sarif` (`SarifWriter`). `--output <file>` writes through the project's output path policy, so the file must lie under an
  `outputs.allow` root (redirect stdout otherwise).
- **generate:** `--pack` (repeatable), `--force`, `--roots`, `--hand-edits`, `--dry-run` (the plan as `A`/`M`/`D`/`H`/`K`/`O`/`C`
  lines, `C` being a conflict), `--diff` (with `--dry-run` or `--check`), `--check` (committed roots, in memory), `--format
  text|json` and `--no-wait` (`LockMode.Fail`: exit 4 when another run holds the lock; the default waits). An apply also lists its
  changes on stdout unless quiet. The summary counts added, modified, deleted, unchanged (files the writer compared and left alone),
  units skipped and hand edits; `--verbosity detailed` adds stage timings. The JSON result leaves out the run id and timings so
  it is deterministic.
- **generate --watch:** the `FileSystemWatcher` is started first (so edits saved during the initial run queue one follow-up run),
  then one incremental run; afterwards the watcher feeds the engine's `GenerationWatcher`
  (250 ms debounce, `RefreshAsync` of the changed paths, one incremental run; engine-owned paths ignored; a watcher buffer overflow
  triggers a run that rescans by stat). Each run prints its change list and summary; the "unchanged" count is per run. Ctrl+C
  stops it with exit 0.
- **migrate:** format 1 → "Model format 1 is current." exit 0; newer → exit 4; missing file, invalid JSON or a missing or
  non-integer `formatVersion` → exit 1.
- **pack new:** `--from empty` writes a canonical `pack.json` (one `each entity` unit with output `{{ kebab entity.name }}.txt`),
  `entity.scriban` and `helpers.js`; `--from sql-ddl|csharp-dapper` copies that starter pack and renames it. Names must be
  kebab-case; an existing folder is refused (exit 4).
- **bench:** `--out --seed --entities --relations --enums --fanout --keep --baseline --max-regression --format` and the global
  `--jobs` (default 8) map onto `BenchmarkOptions`, with the synthetic generator's minimums (`--entities` 2, `--enums` 1,
  `--fanout` 1, `--relations` at most half the entity pairs); other values exit 4 with a message. The report is printed with
  `BenchmarkReportJson.ToText`/`Write`, the same text and JSON as `bench/Maquettiste.Bench`, so a CLI JSON report is a valid
  `--baseline`. Exit 0 when `BenchmarkReport.Passed`, 2 when a budget, the regression gate or the check run failed, 4 when the
  benchmark could not complete (`BenchmarkException`, printed without a stack trace). The harness deletes `staged/` and
  `pipelined/` under `--out`, so the CLI only accepts an `--out` folder that is new, empty or holds its `.maquettiste-bench`
  marker, which it writes through `OutputPathPolicy` (cache target).

## Runtime settings

The csproj sets server GC (concurrent, no dynamic adaptation), tiered PGO off and call counting from the first call, for the
SPEC Section 13 budgets; see `bench/README.md` ("Runtime settings") for the measurements. Peak memory: server GC keeps one heap per
core, so a cold `generate` of the 100,050-file benchmark repo (`--jobs 8`) on 24 cores takes about 9.3 s with a peak resident set
of about 4.9 GB, against about 14 s and 1.6 GB with workstation GC (`DOTNET_gcServer=0`); `DOTNET_GCHeapCount=8` gives about 10 s
and 2.8 GB, at the cost of longer GC pauses in later incremental runs (why the csproj does not cap it). `--watch` keeps that
footprint. A one-shot `generate` after one entity edit takes about 3.2 to 4.1 s (process start, JIT, index-cache load and a full resolve; the 2 s incremental budget is met by
the long-lived `--watch` and editor processes, see `bench/README.md`). They apply to this process only; a host embedding the
engine keeps its own settings. ReadyToRun is not enabled for the tool package (same section).

## Deviations and notes

- The example packs `packs/sql-ddl` and `packs/csharp-dapper` are still empty (W10), so nothing is embedded yet; `init` and `pack
  new --from` then use small built-in stand-ins (`StarterPacks.Fallback`: one `CREATE TABLE` per table; for C#, one partial record
  per entity and per value object and one enum per enum, `required` on required properties, `#nullable enable`, a `types/csharp.json`
  map; InitTests compiles the billing output with warnings as errors) and say "built-in starter". The embedded items carry
  `WithCulture="false"` so a template such as `entity.cs.scriban` stays in the main assembly. Once W10's packs exist they are embedded and used instead;
  their output paths are prefixed with `db` and `src/Generated` by the `packs.<name>.output` that `init` writes, like the billing
  fixture's settings.
- A repo without `.maquettiste/maquettiste.json` makes `validate`, `generate` and `pack new` exit 1 ("no model found"), as the
  design specifies for `migrate`.
- `init` creates only the phase 1 model folders; S4's `processes/`, `operations/` and `seeds/` belong to phases 3 and 4.
- `--no-wait` and the `C` letter are additions to design section 16 (a busy-lock exit code needs a way to ask for
  `LockMode.Fail`; conflicts need a letter).
- `--quiet` is `--verbosity quiet`: no progress, no summary, no apply listing, and only error diagnostics.
