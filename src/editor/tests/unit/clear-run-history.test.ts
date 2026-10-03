// Clearing the run history (DELETE /api/jobs): the selection the Generate screen keeps afterwards, and the mock queue's clear.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import type { JobClock } from "@/mocks/model/jobs";
import { selectionAfterClear } from "@/realtime/jobs";

/** A clock whose timers run only when flushed. */
function manualClock(): JobClock & { flush(): void } {
  const timers: (() => void)[] = [];
  return {
    now: () => new Date("2026-10-02T12:00:00Z"),
    setTimeout: (fn: () => void) => timers.push(fn),
    flush: () => {
      for (let i = 0; i < 10_000 && timers.length; i++) timers.shift()!();
    },
  };
}

describe("selectionAfterClear", () => {
  const selection = { planJob: "P", planId: "PLAN", applyJob: "A" };

  it("drops finished jobs and their plan", () => {
    expect(selectionAfterClear(selection, () => false)).toEqual({ planJob: null, planId: null, applyJob: null });
  });

  it("keeps the plan an apply still running applies", () => {
    expect(selectionAfterClear(selection, (id) => id === "A")).toEqual({ planJob: null, planId: "PLAN", applyJob: "A" });
  });

  it("keeps a plan job still running", () => {
    expect(selectionAfterClear({ planJob: "P", planId: null, applyJob: null }, (id) => id === "P")).toEqual({
      planJob: "P",
      planId: null,
      applyJob: null,
    });
  });
});

describe("MockJobQueue.clearHistory", () => {
  it("removes finished jobs and their plans, and keeps a queued apply and its plan", () => {
    const clock = manualClock();
    const backend = new MockBackend({ clock });
    const first = backend.jobs.enqueue("plan", { plan: {} })!;
    clock.flush();
    const second = backend.jobs.enqueue("plan", { plan: {} })!;
    clock.flush();
    const kept = backend.jobs.get(second.id)!.planResult!.plan!.id;
    const dropped = backend.jobs.get(first.id)!.planResult!.plan!.id;
    const apply = backend.jobs.enqueue("apply", { planId: kept })!;

    expect(backend.jobs.clearHistory()).toEqual({ jobs: 2, plans: 1 });
    expect(backend.jobs.list().map((j) => j.id)).toEqual([apply.id]);
    expect(backend.generation.getPlan(dropped, false)).toBeNull();
    expect(backend.generation.getPlan(kept, false)).not.toBeNull();
  });
});
