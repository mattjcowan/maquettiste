# bench

**Owner:** W11 Bench. See docs/engineering/engine-design.md sections 17 and 18.

`Maquettiste.Bench`: a console app whose public `SyntheticModelGenerator` and `BenchmarkHarness` the CLI's `bench` command also
calls; `bench/packs/fanout/` (embedded with `packs/sql-ddl` and `packs/csharp-dapper` as `Maquettiste.Bench.Packs/<pack>/<path>`,
so an installed tool can write them); `bench/baseline.json` (the reference report for the 10% regression gate, committed only
when every budget passes on the reference machine). Tests: `tests/Maquettiste.Bench.Tests`.

## Running it

```
dotnet run -c Release --project bench/Maquettiste.Bench -- [--out <dir>] [--jobs 8] [--seed 42] [--entities 5000]
    [--relations 20000] [--enums 500] [--fanout <n>] [--keep] [--baseline bench/baseline.json] [--max-regression 10]
    [--format text|json] [--report <file>] [--no-example-packs] [--cli <maquettiste executable> | --no-one-shot]
```

`--cli` names the `maquettiste` the one-shot figure runs (a packed tool, for ReadyToRun numbers; default: this app itself in its
`one-shot-generate` mode, see "One-shot CLI"); `--no-one-shot` skips the figure. `Maquettiste.Bench one-shot-generate --repo <dir>
--cache-dir <dir> [--jobs <n>] generate [--quiet]` is that mode: one apply run in a fresh process, as `maquettiste generate` runs it
(new model store, the engine's last-run record on), with the CLI's exit codes.

Exit codes: 0 pass; 2 a budget, the regression gate, the `--check` run or the determinism cross-check (the two cold runs
must write identical, non-empty manifests) failed; 4 usage error, invalid model or pack, a `--baseline` file that does not
exist, an I/O error, or cancellation. Progress (one line per stage) goes to stderr; the report goes to stdout (`--format`) and
to `--report` as JSON. `--out` defaults to a temporary folder, deleted afterwards unless `--keep`; `bench/out/` and
`bench/results/` are gitignored. The harness marks the `staged/` and `pipelined/` folders it creates under `--out` with a
`.maquettiste-bench` file and only ever replaces or deletes folders carrying that marker: an existing non-empty `staged/` or
`pipelined/` without it stops the run (exit 4) and is left untouched. Every folder it creates or deletes goes through the
engine-write guard (`OutputPathPolicy.CheckEngineWrite`, `WriteTarget.Cache`, rooted at the work folder).

Model limits: at least 2 entities, 1 enum, 1 value object and 1 scalar type, and at most one relation per entity pair
(`n(n-1)/2`). Above half the pairs the generator draws a seeded shuffle of every pair instead of sampling, so dense models do
not keep the package-local shape. `maquettiste bench` accepts `--entities 1` and `--enums 0`; the harness refuses those
(exit 4 from this app).
`.github/workflows/bench.yml` runs it on `v*` tags and on manual dispatch.

## What it measures

1. `SyntheticModelGenerator.WriteAsync` writes the same seeded model twice (`<out>/staged/repo`, `<out>/pipelined/repo`), each
   with its own cache folder, through the canonical writer and the engine-write guard.
2. **Cold, stage barriers** (`GenerationRequest.StageBarriers = true`) on `staged`: per-stage timings from the engine's
   `StageTiming`, which the budgets use.
3. **Cold, pipelined** on `pipelined` (fresh repo and cache): the end-to-end total (`cold-total`).
4. **Incremental**: `SyntheticModelGenerator.EditOneEntityAsync` rewrites one entity file (its first non-key attribute gets a
   description and flips `required`), then the same `GenerationService` and `ModelStore` run again (the editor and watch
   scenario: the store rescans by stat).
5. **Check** (`GenerationMode.Check`): must report no drift.
6. **Incremental, new store** (reported, not a budget): `SyntheticModelGenerator.RevertEditAsync` writes the entity back as
   generated, and a new `ModelStore` and `GenerationService` over the same repo and cache run once: a new host process's first
   run (index cache, unit states and manifests read from disk, full resolve), but with a warm JIT. The report's
   `incremental.freshStore` and the text line "Incremental, new store" carry it.
7. **One-shot CLI** (reported, not a budget; after step 6): an untimed fresh process runs `generate` over the pipelined repo and
   its cache folder (`BenchmarkOptions.OneShotCommand` plus `--repo --cache-dir --jobs generate --quiet`) with nothing changed, which
   leaves the engine's last-run record; then the same one-entity edit again and a timed fresh process (which checks and rejects
   that record first, as a real edit after a no-op build does), then a second timed fresh process with nothing changed. A report
   note says whether the untimed run left a record. The report's `incremental.oneShot` (`command`, `editSeconds`,
   `noOpSeconds`) and the text line "One-shot process" carry them; a non-zero exit fails the benchmark (exit 4) with the end of
   its stderr. `maquettiste bench` runs itself; the bench app runs itself in `one-shot-generate` mode (JIT) unless `--cli` names
   another executable.

| Budget (SPEC section 13) | Limit | Measured as |
| --- | --- | --- |
| `load-validate-resolve` | 3 s | load + validate + resolve |
| `plan` | 2 s | plan (pack load and unit planning) + skip |
| `render` | 40 s | render |
| `post-process-write` | 15 s | post-process (wall) + write |
| `cold-total` | 60 s | the pipelined cold run, end to end |
| `incremental` | 2 s | the incremental run, end to end (same store: the editor and `--watch`) |
| `files` | ≥ 100,000 | files the cold run wrote |

Stage times are busy times (the sum of a stage's spans) except post-processing, which runs units in parallel and counts wall
time. The engine records `plan` twice (pack loading before resolve, unit planning after it), so its `StageTiming.Wall` also
covers resolve and cannot be used.

The regression gate compares each time budget with the same budget in the baseline report; a budget fails when it is more than
`--max-regression` percent and more than 50 ms slower (250 ms for `incremental`, whose run-to-run spread on the reference machine
is about ±0.2 s, more than 10% of it). The gate only means something on the machine class the baseline was recorded on, so it runs
only when the report's machine matches the baseline's: cores, `--jobs`, OS, architecture, server GC, and (when the baseline records
them) the GC heap count and the runtime-overriding `DOTNET_` variables. Otherwise it is skipped, not failed: the report's
`regressionGateSkipped` says why, a note repeats it, and `bench.yml` turns it into a `::warning::`.
**`bench/baseline.json`** is the report of a default run (`--jobs 8`, seed 42) on the machine the phase 1 budgets were met on
(24-core x64, Ubuntu 22.04 under WSL2, .NET 10.0.9): the median run of three, all three passing every budget, with each budget's
`actual` set to the median of the three. A slower machine
class (a CI runner) should record its own baseline before relying on the gate. `bench.yml` passes `--baseline` only when the file
exists and otherwise emits a `::warning::`; an explicit `--baseline` path that does not exist fails the run.

## The synthetic model (seed 42 by default)

50 packages; 5,000 entities (8 to 20 attributes, 10% in two-level hierarchies stored TPT, `audited` and `soft-delete` on 30% of
the rest); 20,000 relations (70% one to many, one in five of those a composition; 15% many to many; 10% many to many with
attributes; 5% one to one; 60% inside one package); 500 enums; 250 value objects; 50 scalar types; three databases
(PostgreSQL `main` with every package, SQL Server `reporting` with the first fifth of the packages, SQLite `edge` with the first
twenty-fifth); mappings storing an enum as its code in `main` for one entity in ten and a value object as JSON in `reporting`
for some entities. Names come from fixed word lists, ids from a seeded SplitMix64 generator (valid ULIDs), so the same seed
gives byte-identical files on any machine. No processes (phase 3).

## The fanout pack

`bench/packs/fanout/`: `n` units per entity (default 20, so the default model yields 100,050 files) cycling through seven
templates (a C# record, a TypeScript DTO, a C# validator, a Markdown page, a SQL query over the entity's PostgreSQL table, a C#
navigation class, a JSON descriptor) plus one index per package. Unit `u05`, `u10`, `u15` and `u20` write to the committed
root `gen/committed`, the rest to the built root `gen/built`. The TypeScript, Markdown and JSON templates call JavaScript
helpers (`helpers.js`), so the Jint sandbox is on the hot path. The generator writes `pack.json` for the requested fanout; the
checked-in `pack.json` is the default's copy (a test keeps them equal). Template names avoid culture-like segments such as
`.cs.` and the csproj marks the embedded packs `WithCulture="false"`: MSBuild would otherwise move `x.cs.scriban` into a
Czech satellite assembly.

## Runtime settings

`Maquettiste.Bench.csproj` (and the CLI's `Maquettiste.Cli.csproj`) set the process's runtime for the SPEC Section 13 budgets:

| Setting | Value | Why (measured on the synthetic model, 24-core WSL2, `--jobs 8`) |
| --- | --- | --- |
| `ServerGarbageCollection` | `true` | The resolved model and the load keep hundreds of megabytes alive; workstation GC spent about 1 s of a 2.5 s resolve in gen1 collections. |
| `ConcurrentGarbageCollection` | `true` | Background gen2 collections, so a large heap does not stop the run. |
| `GarbageCollectionAdaptationMode` | `0` | Dynamic adaptation (DATAS) starts server GC with one heap and grows it, which gave back most of the gain on a short run. |
| `TieredPGO` | `false` | Instrumented tier-0 code roughly doubled the cold load's CPU time on eight parallel readers (cold load 3.7 s with PGO, 2.3 s without). |
| `System.Runtime.TieredCompilation.CallCountingDelayMs` | `0` | Hot methods start counting calls at once and reach tier 1 during a cold run (cold load 2.0 s instead of 2.3 s). |

These settings belong to the process: a host that embeds the engine (the functions host of phase 2) keeps its own GC and JIT
settings. Server GC takes one heap per core, so memory grows with the machine: on 24 cores the benchmark's peak working set is
about 5.9 GB and a cold CLI `generate` of the benchmark repo peaks at about 4.9 GB resident (1.6 GB with workstation GC, but
about 14 s instead of 9 s); an 8-core laptop has 8 heaps.

**The heap count is not capped (round 3).** Capping it at eight (`System.GC.HeapCount`, with `System.GC.NoAffinitize`) was
measured and left out: it lowers the benchmark's peak to about 3.6 GB and a cold CLI `generate` to about 2.8 GB at about the same
cold times, but the incremental run, which follows the cold run and reclaims its garbage, then pauses for GC 0.15 to 0.8 s instead
of about 0.03 s, and two runs in eight took 2.03 and 2.07 s (the budget is 2 s; medians about 1.35 to 1.45 s either way). A heap count of 16 gave
5.0 GB and short pauses. The report now records the incremental runs' GC pauses (`incremental.gcPauseSeconds`), since they are
the main run-to-run noise. The same numbers matter for the SPEC's 8-core laptop, which has eight heaps whatever the setting: there
the incremental run is expected to show those pauses (see "Deviations and caveats").

The same benchmark under other runtime configurations (24 cores, `--jobs 8`, set through environment variables, which the report
records under `machine.runtimeEnvironment`; one run each, round 3):

| Configuration | load-validate-resolve | incremental (same store) | incremental (new store) | peak |
| --- | --- | --- | --- | --- |
| The csproj settings above (median of three) | 1.72 s | 1.43 s | 1.38 s | 5.9 GB |
| The same with eight heaps (`DOTNET_GCHeapCount=8`) | 1.83 s (median of three) | 1.35 s (median; 2 of 8 runs 2.03 and 2.07 s) | 1.64 s | 3.6 GB |
| .NET defaults with server GC (`DOTNET_GCDynamicAdaptationMode=1 DOTNET_TieredPGO=1 DOTNET_TC_CallCountingDelayMs=64`) | 2.92 s | 1.76 s | 1.84 s | 2.2 GB |
| Workstation GC (`DOTNET_gcServer=0 DOTNET_TieredPGO=1 DOTNET_TC_CallCountingDelayMs=64`) | 4.07 s (over) | 3.18 s (over) | 4.17 s | |

So the budgets need server GC; the other knobs are margin (the defaults leave about 3% on the cold stage budget and 12% on the
incremental one). `docs/engineering/host-contracts.md` records this as an open point for the functions host.

**Which incremental run the 2 s budget covers.** SPEC Section 13 places incremental runs in watch mode and the editor: a
long-lived process whose model store, resolver and JIT are warm. That is the `incremental` budget. A one-shot CLI process (CI and
build integration, SPEC Section 17) pays more; the report's one-shot figure tracks it, and "One-shot CLI" below gives the numbers
and what remains.

## One-shot CLI (WB)

What a fresh `maquettiste generate` process pays on the kept benchmark repo (fanout only, `--jobs 8`, 24-core WSL2), measured with
the packed tool installed from its package folder (`dotnet pack src/Maquettiste.Cli -c Release -o <pkg>`, then
`dotnet tool install --tool-path <dir> --add-source <pkg> Maquettiste.Cli --version 1.0.0-alpha.1`), each figure the median of
interleaved runs (before and after alternate, an edit run then a no-op run each round):

| Figure (packed tool, `--jobs 8`) | Before | After, portable package (JIT) | After, ReadyToRun package |
| --- | --- | --- | --- |
| `generate` with nothing changed since the last apply | 2.92 s | 0.26 s | 0.25 s |
| `generate` after a one-entity edit (24 units render) | 3.37 s | 3.30 s | 2.86 s |

Medians of five interleaved rounds at a load average of 2 to 5 from other work on the machine (an earlier series of five at a load
of 5 to 7 gave 2.95 and 3.36 s before, 0.27 and 2.95 s after with ReadyToRun). The harness's own figure, three runs of `dotnet run
--project bench/Maquettiste.Bench -- --jobs 8 --no-example-packs --cli <tool>/maquettiste` with the ReadyToRun tool (the bench's
edit renders 57 units): after the edit 2.84, 2.89 and 2.93 s (median 2.89 s), with nothing changed 0.209, 0.213 and 0.223 s
(median 0.213 s); the same runs' in-process figures passed gate 1 (load-validate-resolve median 1.92 s, incremental 1.32 s, cold
total 6.58 s). The load-validate-resolve median is above the baseline's 1.72 s (resolve 1.02 s there, about 1.2 s in these runs);
the machine may have been busier than when the baseline was recorded, but that is not shown (a three-round A/B at a load of 4 to
22 cannot resolve 0.1 s, and the baseline has not been re-recorded on an idle machine), so the gap is unexplained. With the bench
app's own `one-shot-generate` mode (JIT, one run): 3.29 s and 0.228 s. Those harness figures predate the review fix to step 7:
their edit run had no record to reject, while the packed-tool edit row above, measured after a no-op run, includes rejecting one
(about 0.06 s), so the two edit figures are not the same measurement. After the review fixes, three harness runs with the bench
app's own `one-shot-generate` mode (JIT, `--no-example-packs`, load average 6 to 7; the edit run now rejects a record first):
after the edit 3.171, 3.207 and 3.209 s (median 3.207 s), with nothing changed 0.213, 0.218 and 0.218 s (median 0.218 s);
in-process medians load-validate-resolve 1.825 s, resolve 1.073 s, incremental 1.397 s, cold total 6.896 s, all budgets passing
and the cold outputs of the two cold runs identical.

What changed:

- **Last-run record** (engine, `src/Maquettiste.Engine/Generation/README.md`): a one-shot apply run of the same engine build whose
  model files, unit-state and built-root manifest files and outputs have the stats the last apply recorded, and whose templates,
  committed manifests and snapshots have the same content, is answered without loading, validating, resolving or planning, with
  the full run's result. That is the whole no-op gain. The review fixes (engine build in the key, trailer checked first, content
  hashes) cost about 13 ms of the no-op run (0.231 to 0.244 s, medians of seven interleaved rounds of the bench app's JIT
  `one-shot-generate` mode; the packed-tool rows above predate them). The recording run pays about 60
  ms (the skipped units' outputs are encoded beside rendering; the rest is checking the saved states and writing about 7 MB); a
  run whose record does not match pays about 60 ms to find out, most of it the thread pool starting, which the load would
  otherwise pay (its index read dropped by about as much). A persisted resolved model was considered and rejected
  (engine-design.md D45).
- **Load** (fresh process, ReadyToRun): the index cache's schema-set hash no longer builds every JsonSchema.Net schema (about
  120 ms to 13 ms); the cache's SHA-256 checks run in parallel (about 97 ms to 45 ms); elements' dependency hashes are computed on
  the parallel readers (about 60 ms off the sequential assembly); the snapshot indexer's per-type metadata lookups no longer take a
  lock from eight walking threads. The load of 26,267 files went from about 0.8 s to about 0.6 s.
- **Resolve**: the resolver's list of objects to freeze no longer takes a lock per resolved object (about 330,000, from the parallel
  phases). The resolved model is unchanged (the cold output tree of the benchmark is byte-identical before and after).
- **Progress**: the CLI's reporter no longer takes its lock for updates that change nothing on screen (26,267 load updates from
  eight readers, 100,050 skip updates).
- **Writes**: a unit-state file whose bytes did not change is not rewritten (28 MB on every no-op run before).
- **ReadyToRun tool packages** (`src/Maquettiste.Cli/README.md`, "Packages"): about 0.4 s of the edit run (table above); a full
  no-op without the record measured about 0.1 s faster (single runs, before the record existed).
- **Tiered compilation** (measured on the ReadyToRun tool, five interleaved rounds, edit run medians): the csproj settings (call
  counting from the first call, PGO off) 2.86 s; .NET's default 100 ms call-counting delay 3.12 s; tiered compilation off 3.08 s
  (no-op 0.235 s against 0.255 s); a call-count threshold of 5 2.95 s. The settings are kept.

What remains of the edit run (about 2.6 to 2.9 s, ReadyToRun, one run instrumented; the parts are sequential): process start
and CLI parsing about 0.05 s; the record check that fails about 0.06 s (mostly the thread pool starting); load about 0.6 s (index
read 0.05, listing 0.05, parse of 26,267 files on eight readers 0.25, snapshot assembly and indexes 0.24); resolve about 1.3 s
(conceptual 0.2 beside validation, then the three database runs one after another: each spends about 0.1 s building entity tables,
0.1 s on relations and 0.1 s finishing tables, and the one gen0 collection of the run, about 0.2 s, lands in one of them; the
process allocates about 2.2 GB); plan and skip about 0.18 s; rendering the 24 changed units with a cold renderer about 0.2 s (the template member catalog's reflection, Scriban and Jint start-up);
closing the pack about 0.25 s (walking and saving 100,050 manifest entries, encoding and writing 28 MB of unit states); the record
about 0.06 s; process exit about 0.1 s (unmapping about 1.7 GB). Under 2 s would need the database runs to overlap (a database run
reads relation keys earlier runs added, `src/Maquettiste.Engine/Resolution/README.md`), or a persisted parsed snapshot, or
incremental resolution; none is in this round. A larger gen0 budget (`DOTNET_GCgen0size`) removes the collection but not the time
(fresh pages cost about the same).

## Table summaries and table detail (`time-tables`)

`dotnet run -c Release --project bench/Maquettiste.Bench -- time-tables --model <dir> [--jobs 8] [--rounds 3]` times the explorer's
table summaries (E5c) and one table's detail (E5f) for every database of a model written by `write-model`: round 1 cold, every later
round after an edit (it rewrites one entity's description through the model store, so run it on a scratch copy), then the parts
(validation, each database's own resolve, the conceptual layer alone, the whole-model resolve). The numbers are in
docs/engineering/explorer-redesign.md section 4.5.

## Deviations and caveats

- The example packs (`sql-ddl`, `csharp-dapper`) are written into the synthetic repo, as the design asks, so the default run
  generates 127,575 files from 122,826 units and its `incremental` budget fails (about 4.3 s, see "Incremental run with the
  example packs"). `--no-example-packs` measures fanout alone (100,050 files), which is what `bench/baseline.json` and the
  phase 1 gate were recorded with; a baseline for the default run has to be recorded separately.
- `--jobs` caps `GenerationRequest.Jobs` and `EngineOptions.MaxDegreeOfParallelism`; GC and I/O threads are not capped.
- The pipelined cold run runs in the same process after the barrier run, so its JIT is warm; the barrier run is the truly
  cold one.
- Peak working set is the process's peak over all five runs.
- On eight GC heaps (an 8-core machine, or `DOTNET_GCHeapCount=8` here) the incremental run's median stays about 1.35 s, but a GC
  pause landing in it (reclaiming the preceding cold run's garbage) pushed 2 of 8 runs just over 2 s. The benchmark runs the
  incremental right after a cold full run, the worst case; a watch process's later edits follow smaller runs.
- The engine reports a cancelled run as `RunOutcome.Cancelled`; the harness turns it into `OperationCanceledException`.
- The fanout pack's C# is closer to compiling (only records with no derived entity are `sealed`, required-null checks only on
  reference types, navigation targets fully qualified) but is never compiled: enums, value objects and `Ulid` are referenced
  without being generated.
- `BenchmarkReport` keeps its scaffolded positional members; everything else (units rendered, check result, regressions,
  machine details) is added as init-only properties.

## Incremental run with the example packs (WA)

The default run (with `sql-ddl` and `csharp-dapper`) re-renders 93 units after the one-attribute edit, against 57 for fanout
alone, and 17 files change. By pack and unit (entity `DebitOffer`, 17 attributes, in package `Orders`, mapped in `main` only):

| Pack / unit | Rendered | Why it renders | New bytes |
| --- | ---: | --- | ---: |
| `fanout` (all) | 57 | as with `--no-example-packs`: the entity's 20 units, its package index, and units of entities whose navigations read it | 12 |
| `csharp-dapper/entity`, `repository` | 4 + 4 | the entity and the three entities related to it (their navigations read it) | 1 + 1 |
| `csharp-dapper/registrations` | 1 | `for: model`, reads every entity (one file per package through file blocks) | 0 |
| `sql-ddl/table` | 18 | the entity's table and the 17 tables (junction tables included) whose foreign keys reference it (reading `referenced_table` records the whole table's dependencies) | 1 |
| `sql-ddl/schema` | 3 | one per database; enumerating `database.tables` records membership keys that cover every entity | 1 (`main`) |
| `sql-ddl/seed` | 3 | same list enumeration | 0 |
| `sql-ddl/migration` | 3 | `main`'s diff changed (a new `once` migration); on the first run after a snapshot moved, the other two diffs changed too (91 units from the second edit on) | 1 (added) |

The time is not in the unit count but in one unit: `sql-ddl/schema` of `main` writes the `CREATE TABLE` of all 10,005 tables of
that database (9 MB) and has to, since the edited table's DDL is in it. Measured three times each, interleaved with the unchanged
tree on the same machine (`--jobs 8`, medians):

| | Before (WA) | After (WA) |
| --- | ---: | ---: |
| `incremental` (same store) | 6.41 s (6.09, 6.41, 7.25) | 4.31 s (4.28, 4.31, 4.44) |
| incremental, new store | 6.58 s (6.36, 6.58, 7.23) | 4.64 s (4.64, 4.64, 6.01) |
| `cold-total` | 14.42 s | 12.43 s |
| cold `render` (barriers) | 7.30 s | 6.11 s |

The fanout pack alone (`--no-example-packs`) is a control, not a gain: none of the SchemaDiff or generation-run changes run without
`usesSchemaDiff` (only the rendering `ValueList` change does), and its incremental render stage took 0.07 to 0.09 s in all six runs. Its `incremental`, `load-validate-resolve`
and `cold-total` were 1.43 to 1.77 s, 1.91 s (median) and 6.59 to 8.44 s before, and 1.39 to 1.43 s, 1.87 s and 6.61 to 7.12 s
after: unchanged within noise (the before median of 1.52 s includes a run with a 0.29 s GC pause).

Those "After" runs were taken on the build just before the last copy of `GenerationRun.cs` into the tree (same content). Rerun
on the final build after the review fixes (the snapshot parse no longer awaited by the save, parsed snapshots pruned,
cancellation through the diff and the prepare, `column_def` testing `is_primary_key` first), three runs each, interleaved with
the build before those fixes, on the same machine while other work kept the load average at 9 to 14:

| | Before the review fixes | Final build |
| --- | ---: | ---: |
| `incremental` (same store) | 4.58 s (4.39, 4.58, 5.16) | 4.17 s (4.15, 4.17, 4.80) |
| incremental, new store | 5.24 s (4.55, 5.24, 6.35) | 5.47 s (4.44, 5.47, 6.91) |
| `cold-total` | 15.37 s (14.07, 15.37, 15.91) | 13.22 s (11.81, 13.22, 13.34) |
| cold `render` (barriers) | 7.58 s (6.36, 7.58, 11.22) | 6.17 s (5.96, 6.17, 6.32) |

Within noise of each other: the nine runs of the earlier build over the session give a median `incremental` of 4.35 s (4.08 to
5.16). Every run rendered 93 units and wrote 17 files, and the final build's kept outputs (staged and pipelined repos, after the
incremental and new-store runs: sql-ddl scripts, migrations, snapshots, manifests) are byte-identical to the earlier build's.
The budget still fails at about 4.2 to 4.6 s against 2 s. Fanout alone on the final build under the same load (three runs):
`incremental` 1.71 s (1.50, 1.71, 1.96), `load-validate-resolve` 2.17 s (1.88, 2.17, 2.31), `cold-total` 8.12 s (7.76, 8.12,
8.45); every budget passed, though the load makes all three slower than the quiet-machine figures above.
One variant was measured and dropped: parsing the snapshot only after it is written (so a run that never writes parses nothing)
made the same-store incremental median 5.11 s against 4.17 s for the earlier build (six interleaved runs each), because the
next run then follows a freshly allocated snapshot graph (`src/Maquettiste.Engine/SchemaDiff/README.md`).

What changed: the `sql-ddl` pack's cycle-closing foreign-key set was a string searched once per foreign key (quadratic; about
1.2 s of the schema unit, `packs/sql-ddl/README.md`); the schema snapshot of `main` was captured twice, parsed on every run and
serialized after the last file write (about 0.9 s at the end of every incremental run, now serialized while the run renders,
parsed once and diffed faster, `src/Maquettiste.Engine/SchemaDiff/README.md`); plain model lists were copied on every template
read (`src/Maquettiste.Engine/Rendering/README.md`).

What remains of the 4.3 s: about 1.2 s before rendering (load, validation and resolution as for fanout alone, plus about 0.1 s of
schema diff), about 2.5 to 3 s of render wall time that is the `main` schema unit alone (about 2 s single-threaded, the rest is
garbage collection under the benchmark's load), and about 0.2 s after the last write. The other 92 units render beside it on the
other workers. That unit is inherent to the pack as specified (one inline schema script per database) and to the design (a unit
renders whole; there is no reuse of unchanged parts of a unit's output); its cost grows with the database (about 0.2 ms per
table). The 2 s budget can be met on this model only without it: fanout alone takes 1.4 s, and `schemaScript: "include"` halves
the unit (many tables stay inline because the synthetic relations close 4,414 foreign-key cycles). The 29 units that render
without new bytes (neighbour tables, entities and repositories, `registrations`, the other schemas and the seeds) cost CPU on the
other workers, not wall time; rendering fewer of them needs finer dependency keys (per member read, or placement keys for table
lists) in the resolver and the tracking context, which is outside this change.


## Cold load with locales (`write-model --locales`, `time-load`)

`write-model --locales N` (0 to 8, default 0) also writes complete shards for N locales besides `en` (fr, de, es, it, ...): the
written model is loaded once, and every localizable node of the default locale gets every expected field in its domain shard,
with the `src` fingerprint of its source text. The texts draw from their own random stream and nodes are visited in id order, so
the same options give the same bytes; with 0 the model is byte-identical to one written without the option.

`dotnet run -c Release --project bench/Maquettiste.Bench -- time-load --model <dir> [--rounds 3]` times a cold load (a new model
store over an empty cache folder), the localizable-node index and the completeness pass. It only reads the model.

Measured at the write-model defaults (26,616 element files, 146,753 localizable nodes and 199,395 fields per locale, 41 shards and
about 26 MB per locale; 24 cores, WSL2), rounds 2 to 5 (the first is a cold process):

| Model | Load | Node index | Completeness | Total |
| --- | --- | --- | --- | --- |
| No locales | 410-540 ms | - | 0 ms | 410-540 ms |
| `--locales 4` | 1,010-1,300 ms | 230-430 ms | 280-390 ms | 1,720-1,960 ms |

Four complete locales add about 1.2 to 1.4 s to the cold load, against the ≤ 400 ms target of
reference-types-seeds-localization.md section 5: the target is **not met** (see that section for the breakdown and next steps).
