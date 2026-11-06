// Which severities the Problems panel shows (kept per browser as mq.problems.filter): a model with hundreds of notes
// hides its errors unless the notes can be switched off, and the tab's count follows what is shown.
import { useSyncExternalStore } from "react";
import { local } from "@/lib/storage";
import type { ProblemGroup } from "./group";

export interface ProblemFilter {
  errors: boolean;
  warnings: boolean;
  infos: boolean;
}

const KEY = "mq.problems.filter";
const ALL: ProblemFilter = { errors: true, warnings: true, infos: true };
let current: ProblemFilter = { ...ALL, ...(local.getJson<Partial<ProblemFilter>>(KEY) ?? {}) };
const listeners = new Set<() => void>();

export function setProblemFilter(next: ProblemFilter): void {
  current = next;
  local.setJson(KEY, next);
  for (const l of listeners) l();
}

export function useProblemFilter(): ProblemFilter {
  return useSyncExternalStore(
    (l) => {
      listeners.add(l);
      return () => listeners.delete(l);
    },
    () => current,
  );
}

const SHOWN: Record<"error" | "warning" | "info", keyof ProblemFilter> = { error: "errors", warning: "warnings", info: "infos" };

// The one element the panel is narrowed to (set from the line under an editor's title; not kept between visits).
let element: string | null = null;
const elementListeners = new Set<() => void>();

export function setProblemElement(id: string | null): void {
  element = id;
  for (const l of elementListeners) l();
}

export function useProblemElement(): string | null {
  return useSyncExternalStore(
    (l) => {
      elementListeners.add(l);
      return () => elementListeners.delete(l);
    },
    () => element,
  );
}

/** The groups of one element; every group when `id` is null. */
export function onlyElement(groups: ProblemGroup[], id: string | null): ProblemGroup[] {
  return id === null ? groups : groups.filter((g) => g.elementId === id);
}

/** The groups with only the shown severities, and the number of diagnostics the filter hides. */
export function applyProblemFilter(groups: ProblemGroup[], filter: ProblemFilter): { groups: ProblemGroup[]; hidden: number } {
  let hidden = 0;
  const out: ProblemGroup[] = [];
  for (const g of groups) {
    const kept = g.diagnostics.filter((d) => filter[SHOWN[d.severity]]);
    hidden += g.diagnostics.length - kept.length;
    if (kept.length) out.push({ ...g, diagnostics: kept });
  }
  return { groups: out, hidden };
}
