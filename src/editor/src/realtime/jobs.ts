// Jobs from the editor's side (phase2-design.md 4.5): after POST /api/generate/plan or /apply,
// join `job:{id}`, then read GET /api/jobs/{id} once, which closes the race with events sent
// before the join. Leave the group on job.completed, when that read already shows the job
// finished, and when the view that started the job lets go of it (the host allows 100 groups per
// connection). A finished job is reported by its run outcome, never by its state.
import type { QueryClient } from "@tanstack/react-query";
import type { GenerationRequest, JobInfo, RealtimeJobCompleted, RunOutcome } from "@/api/types";
import * as endpoints from "@/api/endpoints";
import { keys } from "@/api/queries";
import type { EditorStore, GenerationState } from "@/state/store";
import type { RealtimeClient } from "./events";

export function isFinished(job: Pick<JobInfo, "state">): boolean {
  return job.state === "succeeded" || job.state === "failed" || job.state === "cancelled";
}

/** The run's outcome: planResult.outcome or applyResult.outcome (null when the job ended without a result). */
export function jobOutcome(job: JobInfo): RunOutcome | null {
  return job.planResult?.outcome ?? job.applyResult?.outcome ?? null;
}

export function outcomeLevel(outcome: RunOutcome | null): "success" | "warning" | "error" {
  if (outcome === "succeeded") return "success";
  if (outcome === "cancelled" || outcome === "busy") return "warning";
  return "error";
}

export function describeOutcome(job: JobInfo | RealtimeJobCompleted): string {
  const outcome = "outcome" in job ? job.outcome : jobOutcome(job);
  const what = job.kind === "plan" ? "Plan" : "Apply";
  if (outcome === null) return `${what} ${job.state}${job.error ? `: ${job.error}` : ""}`;
  return `${what} ${outcome}`;
}

type RunSelection = Pick<GenerationState, "planJob" | "planId" | "applyJob">;

/**
 * The run selection after the run history was cleared (DELETE /api/jobs): every finished job and every plan no queued or
 * running job names are gone, so a finished plan or apply job is dropped, and the plan with them unless an apply of it still
 * runs. A plan job still running stays (its plan comes when it finishes). `live` says whether a job is queued or running.
 */
export function selectionAfterClear(g: RunSelection, live: (id: string) => boolean): RunSelection {
  const planJob = g.planJob && live(g.planJob) ? g.planJob : null;
  const applyJob = g.applyJob && live(g.applyJob) ? g.applyJob : null;
  return { planJob, applyJob, planId: applyJob ? g.planId : null };
}

type Listener = (job: JobInfo) => void;

export class JobTracker {
  private readonly joined = new Set<string>();
  private readonly listeners = new Set<Listener>();
  private readonly lastStage = new Map<string, string>();

  constructor(private readonly deps: { realtime: RealtimeClient; queryClient: QueryClient; store: EditorStore }) {}

  /** Tells `listener` about every job this window follows when it finishes (with the full record). */
  onFinished(listener: Listener): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  /** The run history was cleared: the selection drops what is gone (selectionAfterClear) and the history is read again. */
  historyCleared(): void {
    const { queryClient, store } = this.deps;
    // A job this window never read is kept: only a record seen finished is known to be gone.
    const live = (id: string) => {
      const job = queryClient.getQueryData<JobInfo>(keys.job(id));
      return !job || !isFinished(job);
    };
    const before = store.getState().generation;
    const next = selectionAfterClear(before, live);
    if (next.planJob !== before.planJob || next.planId !== before.planId || next.applyJob !== before.applyJob) {
      store.getState().setGeneration(next);
      if (before.planId && !next.planId && store.getState().diff?.planId === before.planId) store.getState().showDiff(null);
    }
    void queryClient.invalidateQueries({ queryKey: keys.jobs });
  }

  startPlan(request: GenerationRequest): Promise<JobInfo> {
    return this.follow(() => endpoints.startPlan(request), "Plan");
  }

  startApply(planId: string): Promise<JobInfo> {
    return this.follow(() => endpoints.startApply(planId), "Apply");
  }

  private async follow(start: () => Promise<JobInfo>, label: string): Promise<JobInfo> {
    const queued = await start();
    const { realtime, queryClient, store } = this.deps;
    store.getState().log("info", `${label} queued (${queued.id})`, queued.id);
    queryClient.setQueryData(keys.job(queued.id), queued);
    const group = `job:${queued.id}`;
    try {
      await realtime.join(group);
      this.joined.add(queued.id);
    } catch (error) {
      store.getState().log("warning", `Could not follow ${label.toLowerCase()} ${queued.id} live: ${(error as Error).message}`, queued.id);
    }
    const current = await endpoints.getJob(queued.id);
    queryClient.setQueryData(keys.job(current.id), current);
    void queryClient.invalidateQueries({ queryKey: keys.jobs });
    if (isFinished(current)) await this.finish(current);
    return current;
  }

  /** Follows a job again (a view came back to one it started): join, then read it once. */
  async watch(id: string): Promise<JobInfo | null> {
    const { realtime, queryClient } = this.deps;
    try {
      if (!this.joined.has(id)) {
        await realtime.join(`job:${id}`);
        this.joined.add(id);
      }
      const current = await endpoints.getJob(id);
      queryClient.setQueryData(keys.job(id), current);
      if (isFinished(current)) await this.finish(current);
      return current;
    } catch {
      return null;
    }
  }

  /** job.progress: logs each new stage in Output. */
  onProgress(job: JobInfo): void {
    const stage = job.progress?.stage;
    if (!stage || this.lastStage.get(job.id) === stage) return;
    this.lastStage.set(job.id, stage);
    this.deps.store.getState().log("info", `${job.kind === "plan" ? "Plan" : "Apply"}: ${stage}`, job.id);
  }

  /** job.completed: a bounded summary; read the record for the rest. */
  async onCompleted(summary: RealtimeJobCompleted): Promise<void> {
    const { queryClient } = this.deps;
    void queryClient.invalidateQueries({ queryKey: keys.jobs });
    const job = await endpoints.getJob(summary.id);
    queryClient.setQueryData(keys.job(job.id), job);
    await this.finish(job);
  }

  private async finish(job: JobInfo): Promise<void> {
    if (!this.lastStage.has(`done:${job.id}`)) {
      this.lastStage.set(`done:${job.id}`, "done");
      const outcome = jobOutcome(job);
      const level = outcome === null ? (job.state === "cancelled" ? "warning" : "error") : outcomeLevel(outcome);
      const extra =
        job.applyResult?.outcome === "stale"
          ? ` — ${job.applyResult.staleUnits.length} stale units, ${job.applyResult.stalePaths.length} stale paths`
          : job.applyResult?.result
            ? ` — ${job.applyResult.result.filesWritten} written, ${job.applyResult.result.filesDeleted} deleted`
            : "";
      this.deps.store.getState().log(level, describeOutcome(job) + extra, job.id);
      for (const listener of this.listeners) listener(job);
    }
    await this.release(job.id);
  }

  /** Leaves `job:{id}` (job finished, or the view that started it went away). */
  async release(id: string): Promise<void> {
    if (!this.joined.delete(id)) return;
    try {
      await this.deps.realtime.leave(`job:${id}`);
    } catch {
      /* leaving is best effort */
    }
  }

  get followed(): string[] {
    return [...this.joined];
  }
}
