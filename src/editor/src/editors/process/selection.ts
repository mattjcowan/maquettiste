// What is selected inside the process editor (a state on States, a transition on Transitions): the inspector follows
// it (phase-3-design.md 6.2: the State and Transition sections, the Process section when nothing inside is selected).
import { useSyncExternalStore } from "react";

export interface ProcessSelection {
  process: string;
  kind: "state" | "transition";
  id: string;
}

let current: ProcessSelection | null = null;
const listeners = new Set<() => void>();

export function selectProcessNode(next: ProcessSelection | null): void {
  if (current?.process === next?.process && current?.kind === next?.kind && current?.id === next?.id) return;
  current = next;
  for (const l of listeners) l();
}

export function processSelection(): ProcessSelection | null {
  return current;
}

const subscribe = (l: () => void) => {
  listeners.add(l);
  return () => listeners.delete(l);
};

/** The node selected in one process's editor (null when none, or the selection is in another process). */
export function useProcessSelection(process: string): ProcessSelection | null {
  const s = useSyncExternalStore(subscribe, processSelection, processSelection);
  return s && s.process === process ? s : null;
}
