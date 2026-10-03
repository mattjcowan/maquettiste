# Jobs

**Owner:** W6 Planner and orchestration. See docs/engineering/engine-design.md section 18.

The job queue's internals behind `JobQueue` (engine root; host-contracts requirements 23 to 28). Implemented; no stubs left.

| File | Implements |
| --- | --- |
| `JobStore.cs` | job records in `CacheDirectory/jobs/<id>.json` (`JobRecord`: the `JobInfo` and its `JobRequest`), through the engine-write guard; the newest 200 finished records are kept |
| `JobEntry.cs` | a job's mutable state inside the queue |
| `JobRecords.cs` | `JobRecord` |

`JobQueue`:

- `TryEnqueue`: validates the request (a plan job needs `Plan`, an apply job `PlanId`; `ArgumentException` otherwise), refuses
  when `capacity` jobs are queued, assigns a ULID (`EngineOptions.IdGenerator`) and `QueuedUtc` (`EngineOptions.TimeProvider`).
  No disk I/O on the caller's thread: records are written by a loop that `RunAsync` runs (queued, started, finished).
- `RunAsync`: one job at a time, in queue order; plan jobs call `GenerationService.PlanAsync`, apply jobs `ApplyAsync`. State:
  `Cancelled` for a `Cancelled` outcome or cancellation, `Failed` for a `Failed` outcome (with an error message) or an exception
  (its message), else `Succeeded` (the result's own outcome may still be `Invalid`, `Stale`, `Conflicts`…).
- `GetAsync`, `ListAsync`: queued and running jobs from memory (with `QueuePosition`, 0-based, and the latest `Progress`);
  finished ones from memory until their record is written, then from disk; the list is newest first (queued time, then id).
- `Cancel`: a queued job is removed and recorded `Cancelled`; a running one has its token cancelled (the run returns within one
  second). Unknown or finished jobs return `false`.
- `ClearHistoryAsync` (2026-10-02): deletes every finished job record and every finished plan folder that no queued or running
  job names, and returns the counts (`JobHistoryCleared`). Queued and running jobs, records a restart would resume and the plan an
  apply job of theirs names stay; so does a plan folder still being made, and, while a plan job runs, every plan no cleared job
  names (the running job's is one). It holds the record writer's lock, and a finished job whose record is not written yet has its
  pending writes dropped. Applying needs the plan folder, so a plan not yet applied must be made again after a clear.
- `OnProgress`, `OnCompleted`: handlers run one after another; progress goes through a 256-entry channel that drops the oldest
  updates when handlers fall behind; handler exceptions are swallowed.
- Resume after a restart: the first `RunAsync` queues again, ahead of new jobs and in their original order (running ones first),
  the persisted jobs that were queued or running. A job running when the queue stops (`RunAsync`'s token or `DisposeAsync`) is
  not recorded as cancelled: it stays `running` so the next process runs it again, and the generation journal makes that run
  resume. Only `Cancel` records `Cancelled`.

## Deviations and notes

- A finished job's record (and `GetAsync`/`ListAsync` for finished jobs) keeps no per-file or per-unit list, which grows with the
  model (100,000 files at gate-1 scale, tens of MB per record, and `ListAsync` reads up to 200 records): a plan job keeps the plan's
  id, request, packs and diagnostics but not `Plan.Units` or `Plan.Changes` (`GenerationService.GetPlanAsync(id)` has the full
  plan while it is among the 20 kept); an apply job keeps its outcome, stale units and paths, counts and diagnostics but not
  `Result.Changes`. `OnCompleted` handlers get the full result.
- Records are written only while `RunAsync` runs; a job queued in a process that never runs the loop is not persisted.
- During the host's overlap window (old and new builds for up to 15 seconds) a job the old build is still running can be resumed
  by the new build too; the run lock serializes the two runs, and the second finds the work done (an apply of the same plan then
  writes nothing and succeeds; see the Generation README).
- A resumed apply job re-applies its plan: files the interrupted apply wrote are recognized through the journal overlay and the
  manifest, so the apply resumes instead of returning `Stale` (tested with a job left `running` across a restart).
