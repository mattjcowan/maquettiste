# Generation

**Owner:** W6 Planner and orchestration. See docs/engineering/engine-design.md section 18.

Pipeline orchestration, the plan store, the watch hook and the composition root `EngineServices` (engine-design.md sections 4.3,
15 and 18). `GenerationService.cs` and `JobQueue.cs` in the engine root are also W6's. Implemented; no stubs left.

| File | Implements |
| --- | --- |
| `EngineServices.cs` | the composition root: `Create` builds the long-lived components and `EnginePaths`; `CreateRenderer`, `CreateHasher`, `CreatePathPolicy`, `CreateWriter` build per-run ones |
| `GenerationRun.cs` | one run's stages: prepare (1 to 4), skip (5), the streaming render → post-process → write pipeline (6 to 8), snapshots |
| `PlanCapture.cs` | records a plan while its dry run streams (units, outputs, disk hashes, blobs) |
| `PlanStore.cs` | `CacheDirectory/plans/<id>/plan.json` and `blobs/<ContentHash>`; the 20 newest plans are kept |
| `Outcomes.cs` | diagnostics and file decisions → `RunOutcome` (precedence 4, 1, 3, 2) |
| `StageClock.cs` | `StageTiming`s |
| `GenerationWatcher.cs` | the debounced watch hook (internal; for the CLI's `--watch` and the functions' watcher) |

Tests: `tests/Maquettiste.Engine.Tests/Generation/` (a fake renderer that honors the `IRenderer` contract, real everything else).

## EngineServices

- `Create(options)` performs no I/O. `EnginePaths` (`OutputPathPolicy(options, null)`) is passed to every writing component:
  loader, unit state store, manifest store, journal, run lock, snapshot store, plan store and job store.
- Added members (internal): `Plans` (`PlanStore`), `Jobs` (`Jobs.JobStore`), and the test hooks `RendererFactory` and
  `WriterFactory` (tests substitute through `with`). `ModelStore(options)` and `GenerationService(store, options)` go through
  `Create`; their internal overloads take an `EngineServices`.

## Runs (`RunAsync`, and the dry run behind `PlanAsync`)

1. Run lock (`Request.Lock`); busy → `Busy`, cancelled while waiting → `Cancelled`.
2. Unfinished journal read and overlaid on the manifests (every mode), so files an interrupted run wrote are never hand edits.
3. Load (`ModelStore.GetSnapshotAsync`, which rescans by stat), validate (any error → `Invalid`, nothing written), pack load
   (any error → `Invalid`), resolve (any error → `Invalid`), schema diffs (only when a run pack sets `usesSchemaDiff`; in check
   mode a non-empty diff is MQ6018, drift), plan (errors → `Invalid`), manifests (all packs), hasher. The manifests, and outside
   check mode the run packs' unit states, are read on the thread pool from the end of pack loading, beside resolve and plan; the
   run awaits the manifests after planning, and the skip stage awaits the state read before the change detector loads the states
   again (the store reuses what it decoded). A run that stops earlier leaves those reads to finish on their own (their failures
   are observed, never thrown).
4. Skip (stage 5); then the pinned version of each formatter that a unit to render names is verified (MQ6008 → `Invalid`);
   formatters chosen by extension are verified lazily by the post-processor (`FormatterVersionsVerified = false`).
   mode a non-empty diff is MQ6018, drift; a snapshot file that cannot be read is MQ1001 on that file → `Invalid`, where it used
   to escape as an `InvalidDataException`), plan (errors → `Invalid`), manifests (all packs), hasher.
4. Skip (stage 5); then, before anything is rendered, the pinned version of every formatter a unit to render could use is
   verified once (MQ6008 → `Invalid`, nothing written): the formatters units name and, when any unit chooses by extension, every
   configured formatter with extensions, since output paths are known only after rendering. The post-processor then skips its
   own lazy check (`FormatterVersionsVerified = true`). This used to cover named formatters only, so a mismatched by-extension
   formatter failed just its units while every other file was written (WI review). Consequence: a configured formatter that no
   output happens to use must still be installed at its pinned version whenever a by-extension unit renders.
5. Apply only: `Journal.BeginAsync`.
6. Streaming: the renderer's stream (`RenderContext.MaxDegreeOfParallelism = Request.Jobs ?? MaxDegreeOfParallelism`) feeds a
   bounded channel of `2 × jobs` post-processing tasks (at most `jobs` running), which the writer reads in plan order into its own
   bounded queue (256 files). With `StageBarriers` each of stages 6 to 8 completes before the next.
7. Writer context: counts per run pack (so each pack's manifest and unit states are saved as soon as its last unit arrives),
   skipped units, hand-edit policy per pack (`Request.HandEdits` ← `packs.<name>.handEdits` ← `handEdits`; `fail` in check mode),
   `AllPacks` when no pack filter, the request's roots, diffs in dry run only, the journal in apply only.
8. Apply only, when the outcome is `Succeeded` and `Roots` is `All`: snapshots with a non-empty diff are saved at `ToRevision`.
   Then `Journal.EndAsync`. A cancelled or failed apply closes the journal without `end` (`DisposeAsync`), so the next run resumes.

Outcome: errors other than MQ6009, MQ6010 and MQ6018 → `Invalid`; a conflict (or in check mode any hand edit) → `Conflicts`;
in check mode an added, modified, deleted or orphaned file (an owned orphan, `OrphanedOwned`, included: an apply would drop its
manifest line), a stale manifest entry or MQ6018 → `Drift`; else `Succeeded`.
Cancellation returns `Cancelled`; any other exception (an I/O failure while writing) propagates, with the journal left to resume.

## Plans (`PlanAsync`, `ApplyAsync`, `GetPlanAsync`, `GetPlanDiffAsync`, `PreviewAsync`)

- A plan is the dry run above with a capture hook in the post-processing workers: per rendered unit its input hash, sorted read
  keys and every output file (`PlanFile`, with the disk content hash at plan time, `null` when missing); the bytes of each file
  the apply would write (differs from disk and is not an existing owned file) go to `blobs/<ContentHash>` as they pass. Skipped
  units are recorded from their stored state (a plain-hash output whose stat matches is not read again). The plan id is the run id.
  Every produced plan is saved (also `Invalid` ones, so their diagnostics can be fetched); `Busy` and `Cancelled` save nothing.
- `PlanAsync` also stores `plans/<id>/write.json` (before `plan.json`): the effective hand-edit policy of every pack at plan time.
- `ApplyAsync`: unknown plan → `Failed`; a plan with errors → `Invalid`; lock; stages 1 to 4 with the plan's request; `Stale`
  (writing nothing, journal untouched) when any planned unit is missing or its input hash recomputed from its read keys differs,
  when a unit appeared, when any planned path's disk hash differs from `DiskHashAtPlan` (hand edits and region edits) unless the
  file already holds exactly the planned bytes put there by the engine (see below), when a planned path now classifies under a
  different output root (or none: `outputs.allow` changed), when a `HandEdited` or `Conflict` path of the plan belongs to a pack
  whose effective hand-edit policy differs from the one in `write.json` (or `write.json` is missing), or when a planned
  deletion's file no longer matches its manifest hash. Missing blobs → `Failed` before anything is written. Otherwise the
  writer gets one `ProcessedUnit` per rendered plan unit (blobs read lazily per unit; unchanged and kept files as
  `ContentOmitted`), skipped units as `SkippedUnit`s (their stored state when it matches, else rebuilt from the plan with the
  current stat), and `PlannedPaths` = every output path and change path of the plan. Unit states come from the plan's read keys
  and input hashes. `Result.UnitsRendered` counts the plan units rendered at plan time.
- `GetPlanDiffAsync`: from the file on disk now to the planned blob (empty when unchanged or kept; a deleted or hand-edited
  orphan diffs to nothing); `null` when the path is not in the plan. Only paths the plan names are read.
- `PreviewAsync`: stages 1 to 4 for that pack without the run lock or the journal, then `RenderOneAsync` of the planned unit, or
  of a unit built for the element when its `where` would not plan it. Validation errors come back as diagnostics, no files.

## Watch hook

`GenerationWatcher(service, request, debounce, onRun, progress)`: `Notify(paths)` (watcher paths; engine-owned paths such as
`.cache/`, `manifest/`, `snapshots/`, `.schema/` under the model root and staged `.*.tmp` files are ignored), `Trigger()`,
`Attach(store)` (editor, CLI and engine change sets trigger; `Disk` ones, which rescans and refreshes produce, do not) and
`RunAsync(ct)`: after `debounce` without notifications, `ModelStore.RefreshAsync(paths)` then one `RunAsync(request)`;
notifications during a run cause one more run. It is internal (the CLI sees it through `InternalsVisibleTo`); the public API has
no watch member.

## Deviations and notes

- Applying a plan again: a planned path whose disk hash differs from `DiskHashAtPlan` is accepted when its disk hash is the
  planned `ContentHash` and the manifest, with any unfinished journal overlaid, records the planned manifest hash under the unit's
  pack. Those are files an earlier apply of the same plan wrote: one that was interrupted (so a re-queued apply job resumes, as
  host-contracts 26 and 33 require) or one that finished (applying it again writes nothing and `Succeeded`). The design's section
  15 rule (any difference from `DiskHashAtPlan` is `Stale`) would make every resumed apply `Stale`. A file edited after such a
  write is still `Stale`.
- Pinned write settings: the design says the plan stores its request; `Request.HandEdits` is usually `null` (settings decide), so
  a settings change between plan and apply could turn a previewed `Conflict` into an overwrite. `write.json` pins the effective
  policies; a change that affects a hand-edited path of the plan is `Stale`, one that affects none is ignored. Output roots are
  pinned by `PlanFile.Root`.

- Known gap (design request, not a deviation): after an apply writes a `once` migration and advances the snapshot, the migration
  unit renders nothing, so the migration is an owned orphan. The next `--check` reports it as drift (design section 16, SPEC
  section 17) and the next apply drops its `o:` manifest line (D29), so a repository is stable only after a second apply. The
  requested design change (D29, section 12.3): an owned orphan whose file still exists keeps its entry and is reported `Kept`; the
  entry is dropped only once the file is gone. An earlier integration-stage change that stopped check from counting owned orphans
  was reverted (WI review): it made `--check` pass while the next apply still changed the committed manifest.
- Check mode also reports, as `Modified` with the new manifest hash, a committed output whose manifest entry is missing or
  differs although the file on disk already has the new bytes (the committed manifest would change on apply): drift. The writer's
  `Unchanged` decision alone would hide it.
- Schema snapshots are saved only after an apply that `Succeeded` over all roots: a run limited to built roots (or one with
  conflicts) could leave a migration unwritten, and advancing the snapshot would lose it.
- `GenerationRequest.Jobs` sets render and post-processing parallelism; the writer's drain tasks follow
  `EngineOptions.MaxDegreeOfParallelism` (W7).
- `RunOutcome.Failed` is returned only by `ApplyAsync` (unknown plan, missing blobs) and by `PlanAsync` never; internal errors
  are exceptions.

## Integration stage (after the Scriban renderer lands)

Done by WI in `tests/Maquettiste.Engine.Tests/Integration/` (real renderer, public API, billing fixture, Scriban packs in
`tests/fixtures/integration/`), except: golden output of `sql-ddl` and `csharp-dapper` (the example packs are still empty, so
the golden and determinism runs use the `e2e`, `billing-demo` and `migrations` fixture packs instead), the renderer's own
look-ahead bound on a large model, and the benchmark budgets. The original list:

- the billing fixture with `sql-ddl` and `csharp-dapper`: golden output, `--jobs 1` versus `--jobs 8` byte-identical, and
  `tr-TR`, `de-DE` and invariant cultures byte-identical;
- incremental equals forced with real templates: edit one attribute, create a mapping, a table overlay and a relation, each
  followed by an incremental run compared with a `--force` run; a template partial edit re-renders only its users (`t:` keys
  recorded by `include`), and whether the renderer records `t:` for a unit's own template;
- that the renderer records `r:` keys through the tracking context (a mapping created later re-renders the entity's units);
- `select` and `where.script` with real pack scripts, and pack helpers during rendering sharing the pack's sandbox scripts;
- file blocks, `pair` companions, `regions` and `once` through Scriban, including MQ6011 and MQ6015;
- schema-diff migrations: a `for: model`, `mode: once` unit reading `schema_diff` whose `d:` key re-renders it after a model change,
  and the snapshot advancing only after a full successful apply;
- plan → apply with real templates, including a region edit after planning (`Stale`);
- cancellation of a real render within one second (Jint scripts included) and resume through the journal;
- the renderer's `RenderAsync` bounding its own look-ahead (the pipeline bounds everything after it), measured on a large model;
- the benchmark's budgets (`plan + skip ≤ 2 s`, incremental ≤ 2 s) with the real planner on the synthetic model.

## Performance notes (WP, round 2)

- Without stage barriers (`GenerationRequest.StageBarriers` false, every normal run), resolution starts right after the load,
  beside validation and pack loading; its result is used only where the sequential order would resolve (after validation and pack
  loading passed), so diagnostics and outcomes are unchanged, and when an earlier stage fails the speculative resolution is cancelled,
  awaited and ignored: `PrepareAsync` does not return before it has stopped, so it never outlives the run or the run lock (round 3;
  before, a model with a self-embedding value object or an inheritance cycle under TPH kept a background resolution running
  indefinitely after the run had returned Invalid). `SpeculativeResolutionTests` runs pipelined generation over every invalid
  validation fixture and those shapes and checks the run ends Invalid within 30 s with the resolution finished. The per-element resolve progress is replaced by a start and an end update in that mode. With barriers (the
  benchmark's per-stage timings) the stages run one after another as before.
- Beside resolution the run also takes the stats of the outputs the loaded unit states record (`Planning/README.md`), which the
  skip stage uses.
