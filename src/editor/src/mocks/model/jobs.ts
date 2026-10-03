// The mock job queue (engine JobQueue semantics): one running job at a time, at most 16 queued,
// progress per stage published to `job:{id}` as job.progress (the JobInfo with its progress), and
// a bounded job.completed summary when the job finishes. A finished record keeps no per-file or
// per-unit lists, like the engine's. The job's state is `succeeded` whenever the run completed;
// its outcome is planResult.outcome or applyResult.outcome.
import type { ApplyResult, JobInfo, PlanResult, ProgressUpdate, RealtimeJobCompleted, GenerationRequest } from "@/api/types";
import { clone } from "@/lib/json";
import type { MockGeneration } from "./generation";

export interface JobPublisher {
  progress(job: JobInfo): void;
  completed(summary: RealtimeJobCompleted): void;
}

export interface JobClock {
  now(): Date;
  setTimeout(fn: () => void, ms: number): unknown;
}

const STAGES: ProgressUpdate["stage"][] = ["load", "validate", "resolve", "plan", "render", "write"];

export class MockJobQueue {
  private readonly jobs = new Map<string, JobInfo>();
  private readonly requests = new Map<string, { plan?: GenerationRequest; planId?: string }>();
  private readonly order: string[] = [];
  private running: string | null = null;
  private readonly cancelled = new Set<string>();
  /** Milliseconds per progress step. */
  stepMs = 60;

  constructor(
    private readonly generation: MockGeneration,
    private readonly clock: JobClock,
    private readonly publish: JobPublisher,
    private readonly newId: () => string,
    private readonly elementCount: () => number,
  ) {}

  enqueue(kind: JobInfo["kind"], request: { plan?: GenerationRequest; planId?: string }): JobInfo | null {
    const queued = this.order.filter((id) => this.jobs.get(id)?.state === "queued").length;
    if (queued >= 16) return null;
    const id = this.newId();
    const job: JobInfo = {
      id,
      kind,
      state: "queued",
      queuePosition: queued,
      progress: null,
      planResult: null,
      applyResult: null,
      error: null,
      queuedUtc: this.clock.now().toISOString(),
      startedUtc: null,
      finishedUtc: null,
    };
    this.jobs.set(id, job);
    this.requests.set(id, request);
    this.order.unshift(id);
    this.clock.setTimeout(() => this.pump(), 0);
    return clone(job);
  }

  get(id: string): JobInfo | null {
    const job = this.jobs.get(id);
    return job ? clone(job) : null;
  }

  list(): JobInfo[] {
    return this.order.map((id) => clone(this.jobs.get(id)!));
  }

  /**
   * Clears the history as JobQueue.ClearHistoryAsync does: every finished job, and every plan no queued or running apply job
   * names (while a plan job runs, a plan no cleared job names stays too).
   */
  clearHistory(): { jobs: number; plans: number } {
    const live = (id: string) => ["queued", "running"].includes(this.jobs.get(id)!.state);
    const keep = new Set(
      this.order
        .filter(live)
        .map((id) => this.requests.get(id)?.planId)
        .filter((p): p is string => !!p),
    );
    const planRunning = this.order.some((id) => live(id) && this.jobs.get(id)!.kind === "plan" && this.jobs.get(id)!.state === "running");
    const finished = this.order.filter((id) => !live(id));
    const named = new Set(finished.map((id) => this.jobs.get(id)!.planResult?.plan?.id).filter((p): p is string => !!p));
    for (const id of finished) {
      this.jobs.delete(id);
      this.requests.delete(id);
    }
    this.order.splice(0, this.order.length, ...this.order.filter((id) => this.jobs.has(id)));
    const plans = this.generation.deletePlans((id) => !keep.has(id) && (!planRunning || named.has(id)));
    return { jobs: finished.length, plans };
  }

  cancel(id: string): "ok" | "not-found" | "finished" {
    const job = this.jobs.get(id);
    if (!job) return "not-found";
    if (job.state === "succeeded" || job.state === "failed" || job.state === "cancelled") return "finished";
    if (job.state === "queued") {
      this.finish(job, "cancelled", null);
      return "ok";
    }
    this.cancelled.add(id);
    return "ok";
  }

  private pump(): void {
    if (this.running) return;
    const next = [...this.order].reverse().find((id) => this.jobs.get(id)?.state === "queued");
    if (!next) return;
    this.running = next;
    const job = this.jobs.get(next)!;
    job.state = "running";
    job.queuePosition = null;
    job.startedUtc = this.clock.now().toISOString();
    this.renumber();
    this.step(job, 0, 0);
  }

  private renumber(): void {
    let position = 0;
    for (const id of [...this.order].reverse()) {
      const j = this.jobs.get(id)!;
      if (j.state === "queued") j.queuePosition = position++;
    }
  }

  private step(job: JobInfo, stage: number, done: number): void {
    if (this.cancelled.has(job.id)) {
      this.cancelled.delete(job.id);
      this.finish(job, "cancelled", null);
      return;
    }
    const total = Math.max(1, this.elementCount());
    const next = Math.min(total, done + Math.max(1, Math.ceil(total / 3)));
    job.progress = { stage: STAGES[stage], done: next, total, currentPath: null, pack: null };
    this.publish.progress(clone(job));
    if (next < total) {
      this.clock.setTimeout(() => this.step(job, stage, next), this.stepMs);
      return;
    }
    if (stage < STAGES.length - 1) {
      this.clock.setTimeout(() => this.step(job, stage + 1, 0), this.stepMs);
      return;
    }
    this.clock.setTimeout(() => this.complete(job), this.stepMs);
  }

  private complete(job: JobInfo): void {
    if (this.cancelled.has(job.id)) {
      this.cancelled.delete(job.id);
      this.finish(job, "cancelled", null);
      return;
    }
    const request = this.requests.get(job.id)!;
    try {
      if (job.kind === "plan") {
        const result = this.generation.plan(request.plan ?? {});
        const count = result.plan?.changes.length ?? 0;
        job.progress = {
          stage: "write",
          done: count,
          total: count,
          currentPath: result.plan?.changes.at(-1)?.path ?? null,
          pack: result.plan?.changes.at(-1)?.pack ?? null,
        };
        this.finish(job, "succeeded", { plan: result });
      } else {
        const result = this.generation.apply(request.planId!);
        const count = result.result?.changes.length ?? 0;
        job.progress = {
          stage: "write",
          done: count,
          total: count,
          currentPath: result.result?.changes.at(-1)?.path ?? null,
          pack: result.result?.changes.at(-1)?.pack ?? null,
        };
        if (result.outcome === "failed") {
          job.error = `Plan ${request.planId} is unknown or its stored bytes are gone.`;
          this.finish(job, "failed", { apply: result });
        } else this.finish(job, "succeeded", { apply: result });
      }
    } catch (error) {
      job.error = String((error as Error)?.message ?? error).slice(0, 2000);
      this.finish(job, "failed", null);
    }
  }

  private finish(job: JobInfo, state: JobInfo["state"], result: { plan?: PlanResult; apply?: ApplyResult } | null): void {
    job.state = state;
    job.finishedUtc = this.clock.now().toISOString();
    job.queuePosition = null;
    const full = { plan: result?.plan ? clone(result.plan) : null, apply: result?.apply ? clone(result.apply) : null };
    // The record keeps no per-file or per-unit lists (the engine trims them); counts come from the full result.
    if (result?.plan) job.planResult = { ...result.plan, plan: result.plan.plan ? { ...result.plan.plan, units: [], changes: [] } : null };
    if (result?.apply) job.applyResult = { ...result.apply, result: result.apply.result ? { ...result.apply.result, changes: [] } : null };
    if (this.running === job.id) this.running = null;
    this.renumber();
    const diagnostics = full.plan?.plan?.diagnostics ?? full.apply?.result?.diagnostics ?? [];
    this.publish.completed({
      id: job.id,
      kind: job.kind,
      state: job.state,
      outcome: full.plan?.outcome ?? full.apply?.outcome ?? null,
      planId: job.kind === "plan" ? (full.plan?.plan?.id ?? null) : null,
      error: job.error ? job.error.slice(0, 2000) : null,
      counts: {
        changes: full.plan?.plan?.changes.length ?? full.apply?.result?.changes.length ?? 0,
        staleUnits: full.apply?.staleUnits.length ?? 0,
        stalePaths: full.apply?.stalePaths.length ?? 0,
        errors: diagnostics.filter((d) => d.severity === "error").length,
        warnings: diagnostics.filter((d) => d.severity === "warning").length,
      },
      finishedUtc: job.finishedUtc,
    });
    this.clock.setTimeout(() => this.pump(), 0);
  }
}
