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
    [--format text|json] [--report <file>] [--no-example-packs]
```

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
long-lived process whose model store, resolver and JIT are warm. That is the `incremental` budget. A one-shot CLI process pays
more: after one entity edit, `maquettiste generate --jobs 8` on the kept benchmark repo takes about 3.2 to 4.1 s (round 3), of which process start is about 0.3 s, loading 26,267 files from the index cache about 0.9 s, a full
resolve with a cold JIT about 1.1 to 1.4 s, the skip check 0.15 s, the renderer's first templates about 0.35 s and the manifest
and state writes about 0.3 s. The new-store figure in the report (same process, warm JIT) is the part the engine controls. Closing
the rest needs ReadyToRun code (below) or a persisted resolved model, neither in phase 1. ReadyToRun was evaluated for the packed tool and left off: a
RID-agnostic tool package cannot carry ReadyToRun code, RID-specific tool packages (`ToolPackageRuntimeIdentifiers`) change the
published package layout and need the crossgen packs at pack time, and the benchmark runs through `dotnet run`, which JIT-compiles
anyway. The cold run's JIT share it could save is visible in the report: cold resolve about 1.3 s against about 0.7 s warm.

## Deviations and caveats

- The example packs (`sql-ddl`, `csharp-dapper`) are written into the synthetic repo when they are embedded, as the design
  asks; while they are skeletons nothing is written, so today the numbers are the fanout pack's alone. Once they land, the
  file count and every stage time grow, and the baseline has to be re-recorded. `--no-example-packs` measures fanout alone.
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
