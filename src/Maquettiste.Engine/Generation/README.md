# Generation

**Owner:** W6 Planner and orchestration. See docs/engineering/engine-design.md section 18.

Pipeline orchestration, the plan store, the watch hook and the composition root `EngineServices` (engine-design.md sections 4.3,
15 and 18). `GenerationService.cs` and `JobQueue.cs` in the engine root are also W6's. Implemented; no stubs left.

| File | Implements |
| --- | --- |
| `EngineServices.cs` | the composition root: `Create` builds the long-lived components and `EnginePaths`; `CreateRenderer`, `CreateHasher`, `CreatePathPolicy`, `CreateWriter` build per-run ones |
| `GenerationRun.cs` | one run's stages: prepare (1 to 4), skip (5), the streaming render → post-process → write pipeline (6 to 8), snapshots |
| `PlanCapture.cs` | records a plan while its dry run streams (units, outputs, disk hashes, blobs) |
| `PlanEntries.cs` | a plan's complete file list (skipped units' outputs as `NotRendered` or `Kept`), its counts by kind and its units by reason |
| `PlanStore.cs` | `CacheDirectory/plans/<id>/plan.json` and `blobs/<ContentHash>`; the 20 newest plans are kept |
| `Outcomes.cs` | diagnostics and file decisions → `RunOutcome` (precedence 4, 1, 3, 2) |
| `StageClock.cs` | `StageTiming`s |
| `GenerationWatcher.cs` | the debounced watch hook (internal; for the CLI's `--watch` and the functions' watcher) |
| `LastRun.cs` | the last-run record of one-shot hosts (`LastRun`, `RunRecord`): replay of a run whose inputs did not change, and its recording |

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
   `AllPacks` when no pack filter, diffs in dry run only, the journal in apply only. Every run covers every root (spec-errata E42
   removed `GenerationRequest.Roots`).
8. Apply only, when the outcome is `Succeeded`: snapshots with a non-empty diff are saved at `ToRevision`
   (the bytes were serialized on the thread pool since the diff, see "Performance notes (WA)"). Then `Journal.EndAsync`. A cancelled or failed apply closes the journal without `end` (`DisposeAsync`), so the next run resumes.

Outcome: errors other than MQ6009, MQ6010 and MQ6018 → `Invalid`; a conflict (or in check mode any hand edit) → `Conflicts`;
in check mode an added, modified, deleted or orphaned file (an owned orphan, `OrphanedOwned`, included: an apply would drop its
manifest line), a stale manifest entry or MQ6018 → `Drift`; else `Succeeded`. Check renders every root (E42); a block whose
target file is missing without `createFile` (MQ6028, info) is not drift.
Cancellation returns `Cancelled`; any other exception (an I/O failure while writing) propagates, with the journal left to resume.

## Last-run record (one-shot hosts; WB)

`GenerationService.ReuseLastRun` (internal, off by default) is set by the CLI's one-shot `generate` (not `--watch`, not `--dry-run`
or `--check`) and by the bench app's `one-shot-generate` mode. A long-lived host (the editor, `--watch`, the functions host) keeps
its model store warm and neither reads nor writes the record, so its runs are unchanged.

- **Replay.** An apply run (not forced, no stage barriers, not a plan's dry run) of a service whose model store has not loaded yet
  first reads `CacheDirectory/last-run.v1.bin` under the run lock. It is answered from the record when all of these hold: the record
  is intact (SHA-256 trailer, checked before any decoded field is used) and from this engine build (`RunRecord.CurrentBuild`, below);
  its key matches (engine build, repo, model, journal and cache folders, and the request's packs and hand-edit override; key `mq-last-run-3`, roots left it with E42);
  there is no journal file; the enumerated model files (the loader's own enumeration) are exactly the recorded ones with the recorded
  length and last-write time, the referenced sidecars too, and the referenced sidecars that were missing are still missing; the
  content hash of every file under `<ModelRoot>/templates` (relative paths and bytes, dot files included) is the recorded one; the
  files directly in the unit-state folder and the journal folder's `manifest/` (a cache copy of a manifest an earlier release left,
  until the next save moves it) are the recorded ones with the recorded stats; the
  content hash of the files directly in the manifest folder `<ModelRoot>/manifest` and `<ModelRoot>/snapshots` is the recorded one; and every
  output the planned units' states record still has its recorded stat (an owned output only needs to exist). The result is the one a full run gives: `Succeeded`, no changes, 0 rendered, every planned
  unit skipped, nothing written, the diagnostics of stages 1 to 5 (load, validate, pack load, resolve, plan) as recorded, and
  timings for `load` (the input check) and `skip` (the output check). Nothing is written, not even the journal. Progress reports a
  load start and end and a skip end only.
  Checks run cheapest and most likely to fail first after the trailer: the recorded model files' stats (in parallel) before the
  listing, the outputs decoded only once every input matched. A file that cannot be read, or a path the file system refuses (only a
  record whose trailer was made to match could hold one), makes the replay give way to the full run instead of failing the run. A miss costs about 60 ms on the benchmark repo in a fresh
  process, most of it the thread pool starting, which the load then does not pay.
- **Recording.** Any other apply run with `ReuseLastRun` first deletes the record, hashes the templates folder (before the packs
  load, so a template changed during the run shows as changed), and after the run writes a new record when the run `Succeeded`, no
  run pack has a non-empty schema diff (saving the snapshot changes what `d:` keys hash, so the next run is not a no-op), no
  diagnostic has rule MQ6004, MQ6005, MQ6009, MQ6010, MQ6027 or MQ6028 (refused paths, duplicate claims, hand edits, lost regions,
  a block file holding its block twice, a block target missing without `createFile`: what a next run would report again, or a
  file that may appear without any recorded stat changing), no journal file is left, the store's last load produced the run's snapshot,
  and every planned unit's stored state, as the writer saved it (`UnitStateStore.Remembered`), is current: a skipped unit keeps its
  state (same input hash, same outputs); a rendered unit has a new state with the input hash it rendered with. These are exactly the
  conditions under which a full run with the same inputs skips every unit and changes nothing, so a replay equals that run. The
  model files' stats are the loader's (taken before each file was read); the engine folders' stats and the committed files' hash are
  taken after the run; a skipped
  unit's outputs are stamped with the stat the skip stage checked them with (so a file whose time stamp alone changed, found intact
  by its bytes, does not disable the record until its unit renders again). The skipped units' outputs are encoded on the thread
  pool while rendering and writing run; the record is written through `EnginePaths` (`WriteTarget.Cache`), atomically. On the
  100,050-output benchmark it is about 7 MB and costs the recording run about 60 ms.
- **Engine build.** `RunRecord.CurrentBuild` is `H(EngineVersion.Value, the engine assembly's module version id, the entry
  assembly's module version id, the runtime version, the bytes of the application's .deps.json files)`, computed once per process.
  `EngineVersion.Value` alone is a contract version that changes only when rendered bytes could change; a build that adds or fixes a
  validation or resolution rule, or rewords a message, keeps it, and would otherwise replay the old build's diagnostics and outcome.
  The module version id changes with any change to the compiled code (and is kept by ReadyToRun compilation), the runtime version
  with a runtime roll-forward, and the deps files with a dependency's package version. It is in the record's header and in the key.
- **Trust.** The record trusts stats (length and last-write time) only where the design already does: for model files and
  referenced sidecars, as the index cache (§5) takes a file whose stat matches without reading it, and for outputs, as the skip
  check (§11) takes an output whose stat matches as intact. Templates (with partials, helper scripts and pack manifests: what units'
  static hashes are made of), manifests and schema snapshots, which a full run reads on every run and which a checkout or
  a script can rewrite, are compared by content: about 0.06 MB of templates and a 3 MB manifest on the benchmark repo. Unit-state
  files (28 MB there) and any cache copy of a manifest an earlier release left, under the cache and journal folders, are written only by the engine and compared by
  stat: the engine rewrites them only in a run, which follows an input change the record sees on its own, and a rewrite with the
  same content leaves the answer right. A tool that rewrites a model file with the same length inside the same timestamp tick is
  not seen, as with the index cache. The record is keyed by the full repo path (a copy of the cache from another checkout does not
  match) and carries a SHA-256 trailer (a damaged record is ignored); a missing, damaged, foreign or stale record only costs the full
  run.
- **Decision (design correctness rules).** A persisted resolved model (keyed by the snapshot hash, the settings hash and the engine
  version) was considered and not built: the design's caches are either content-addressed inputs (§5 index cache) or outputs of a
  run checked against the disk (§11 unit states), so that a stale, foreign or damaged cache "costs a re-read, never a wrong model"; a
  serialized resolved model would be a third kind, a derived object graph of about 330,000 objects whose every member (internal
  setters, cross references, dependency keys) would need a serializer kept in step with the resolver, where a missed member
  silently changes rendered bytes, and whose decoding in a fresh process would cost about what resolving does (about 1.2 s cold).
  The last-run record instead stores the *outcome* of stages 1 to 5 for the one case where it is fully determined (nothing changed),
  which skips loading and validation as well as resolution. An edited model still resolves in full. Recorded as D45 in
  engine-design.md.

## Plans (`PlanAsync`, `ApplyAsync`, `GetPlanAsync`, `GetPlanDiffAsync`, `PreviewAsync`)

- A plan is the dry run above with a capture hook in the post-processing workers: per rendered unit its input hash, sorted read
  keys and every output file (`PlanFile`, with the disk content hash at plan time, `null` when missing); the bytes of each file
  the apply would write (differs from disk and is not an existing owned file) go to `blobs/<ContentHash>` as they pass. Skipped
  units are recorded from their stored state (a plain-hash output whose stat matches is not read again). The plan id is the run id.
  Every produced plan is saved (also `Invalid` ones, so their diagnostics can be fetched); `Busy` and `Cancelled` save nothing.
- `PlanAsync` also stores `plans/<id>/write.json` (before `plan.json`): the effective hand-edit policy of every pack at plan time.
- A plan's `Changes` name every file (`PlanEntries.cs`, added 2026-10-02): the dry run's writer lists identical files too
  (`WriteContext.ListUnchanged`: `Unchanged`, never with a diff), and the outputs of skipped units are added from their stored
  state, an owned (`o:`) one as `Kept`, any other as `NotRendered` (path, unit key, manifest hash). Sorted by path, pack, kind.
  `Counts` (every kind's JSON name, zero included), `UnitsRendered` and `UnitsSkipped` (units by reason) summarize the plan; a
  job record keeps them when it drops the lists. Apply never acts on `Unchanged`, `NotRendered` or `Kept` entries: it is fed
  from `Units`, which already hold their paths. The MCP `plan` tool leaves `Unchanged` and `NotRendered` entries out of its answer
  (they are counted); `get_plan` and the editor API return every entry.
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
- `GetPlanDiffAsync`: from the file on disk now to the planned blob (empty when unchanged, not rendered or kept, and for any
  owned file that existed at plan time, whatever its unit rendered, such as a migration's placeholder; a deleted or hand-edited
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
- Check mode also reports, as `Modified` with the new manifest hash, an output whose manifest entry is missing or differs although
  the file on disk already has the new bytes (the manifest would change on apply): drift. The writer's `Unchanged` decision alone
  would hide it. Since E42 this covers every output (before, committed roots only); a block compares the hash of its lines (a
  `bc:` entry against a `b:` render is not drift), and a block without an entry whose target file does not exist is left to the
  writer.
- Schema snapshots are saved only after an apply that `Succeeded` (before E42, also only over all roots): a run with conflicts
  could leave a migration unwritten, and advancing the snapshot would lose it.
- Plan reason `target-missing` (E42, `PlanExplainer.TargetMissing`): a rendered `block` unit without `createFile` whose every target
  file was missing at plan time gets that reason and one `target-missing` cause per file ("<path> does not exist; set createFile
  to create it") instead of the usual explanation; the writer writes nothing for it (MQ6028).
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
- file blocks, `pair` companions, `regions` and `once` through Scriban, including MQ6011 (MQ6015 retired with E42);
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

## Performance notes (WB, one-shot CLI)

- The last-run record above: a one-shot `generate` with nothing changed since the last apply takes about 0.26 s on the benchmark
  repo instead of about 2.9 s, median of five interleaved runs of the packed tool (`bench/README.md`, "One-shot CLI").
- `GenerationRun.CollectRenderedInputHashes` (called only when recording) collects each rendered unit's input hash as the writer
  takes it; `GenerationRun.Progress` exposes the run's progress to the replay.
- Review fixes (engine build in the key, trailer first, templates, committed manifests and snapshots by content): the replay reads
  the templates and committed files on two pool tasks beside the model files' stat check, and a miss cancels and awaits them. Cost,
  measured with the bench app's `one-shot-generate` mode (JIT) on the kept fanout benchmark repo, seven interleaved rounds at a load
  average of 3 to 7: no-op 0.231 s before and 0.244 s after (medians); edit 3.10 s before and 3.19 s after (medians; the rounds
  spread from 2.6 to 3.4 s, so the edit difference is inside the noise). One instrumented run of the replay: build identity about
  5 ms (first use, JIT), trailer check about 4 ms, templates hash about 8 ms (11 files, mostly JIT), committed hash about 5 ms (a
  3 MB manifest).

## Watching through a symbolic link (CI fix, 2026-09-28)

A file-system watcher can report paths through the link-resolved form of a configured root: macOS reports a `/var/...` root
as `/private/var/...`. `WatchPaths.ResolveLinks` resolves the configured repo and model roots once, and
`GenerationWatcher.Normalize` maps every reported absolute path back onto the configured form before the engine-owned check
and before the path reaches the store, so a manifest write under the real prefix no longer triggers a second run.
## Performance notes (WA, incremental runs with the example packs)

- **Schema snapshots are serialized while the run renders.** In apply mode, `SchemaDiffsAsync` diffs with
  `SchemaDiffer.DiffAndCapture` (one capture instead of two) and, for each database whose diff is not empty, starts
  `SnapshotStore.Prepare` on the thread pool with that capture stamped `ToRevision` (`PreparedRun.PendingSnapshots`).
  `SaveSnapshotsAsync` awaits the prepared bytes and writes them (`SnapshotStore.WriteAsync`), under the same condition as
  before (apply, `Succeeded`). A snapshot store or differ that is not the engine's own (a test double) takes the old
  path, a capture and `SaveAsync` after the apply. On the benchmark the end of an incremental run follows the last file write by
  about 0.2 s instead of 0.9 s. A run that fails or is cancelled leaves the prepared bytes unused (the task does no I/O; its failure is
  observed). (Before E42 an apply over `--roots committed` or `built` also left them unused.) The prepare task gets the run's token (checked before sorting and before serializing), and the diff's
  parallel capture and compare observe it too, so a cancelled run stops that work at the next table or step. The prepared bytes
  are also parsed back on a separate thread-pool task for the next run's load (`Prepare(…, parse: true, …)`);
  `SaveSnapshotsAsync` waits for the bytes only, never for that parse, so it is off the critical path even when rendering is
  shorter than serializing plus parsing (`SchemaDiff/README.md` has why it is not deferred until after the write). At the end of
  the schema diff the run calls `SnapshotStore.Retain` with its databases (none when no pack diffs), so the store does not hold a
  removed or renamed database's snapshot.
- The diff also runs its capture and unchanged-table check on `EngineOptions.EffectiveParallelism` threads (`SchemaDiff/README.md`).

