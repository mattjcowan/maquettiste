// The navigation intent of the Processes explorer (phase-3-design.md 6.1): a state, event or scenario row opens the
// process editor on the matching tab with that node selected. The explorer writes the intent here; the process editor
// (src/editors/process, round P3b) reads it and clears it once it has selected the node.
import { useSyncExternalStore } from "react";

export const PROCESS_TABS = ["states", "transitions", "events", "gates", "context", "scenarios"] as const;
export type ProcessTab = (typeof PROCESS_TABS)[number];

export interface ProcessFocus {
  process: string;
  tab: ProcessTab;
  /** The state, event or scenario id to select, or null for the tab alone. */
  node: string | null;
  /** Grows with each request, so the same node asked for twice selects it twice. */
  seq: number;
}

let current: ProcessFocus | null = null;
let seq = 0;
const listeners = new Set<() => void>();

export function focusProcess(process: string, tab: ProcessTab, node: string | null): ProcessFocus {
  current = { process, tab, node, seq: ++seq };
  for (const l of listeners) l();
  return current;
}

export function processFocus(): ProcessFocus | null {
  return current;
}

export function clearProcessFocus(): void {
  if (!current) return;
  current = null;
  for (const l of listeners) l();
}

const subscribe = (l: () => void) => {
  listeners.add(l);
  return () => listeners.delete(l);
};

/** The pending intent for one process (null when it is for another, or none). */
export function useProcessFocus(process: string): ProcessFocus | null {
  const focus = useSyncExternalStore(subscribe, processFocus, processFocus);
  return focus && focus.process === process ? focus : null;
}

/** The tab a tree row opens: a state → States, an event → Events, a scenario → Scenarios. */
export function tabOfRow(kind: string | undefined): ProcessTab | null {
  return kind === "state" ? "states" : kind === "event" ? "events" : kind === "scenario" ? "scenarios" : null;
}
