// The simulation's view of the chart (phase-3-design.md 6.4): the active atomic states the canvas highlights and the
// transitions the last accepted input took, whose edges flash once. The simulation panel (session.ts) writes it after
// every result; the statechart canvas (canvas/statechart) reads it. A small external store, so neither side imports
// the other's React tree.
import { useSyncExternalStore } from "react";

export interface SimulationView {
  process: string;
  /** The active atomic states (ids) after the last input, in document order; empty before the first result. */
  configuration: readonly string[];
  /** The transitions (ids) the last accepted input took, in order; empty when it was refused or there is none. */
  taken: readonly string[];
  /** The last input's index in the session (-1: the initial entry), for the canvas to tell a new step from a re-render. */
  step: number;
  /** Grows with every result, so the canvas flashes `taken` once per result. */
  seq: number;
}

let current: SimulationView | null = null;
let seq = 0;
const listeners = new Set<() => void>();

/** Publishes a result's view (the panel), or clears it when the panel closes or the session is reset (null). */
export function setSimulationView(next: Omit<SimulationView, "seq"> | null): void {
  if (!next && !current) return;
  current = next ? { ...next, seq: ++seq } : null;
  for (const l of listeners) l();
}

export function simulationView(): SimulationView | null {
  return current;
}

const subscribe = (l: () => void) => {
  listeners.add(l);
  return () => listeners.delete(l);
};

/** The simulation view of one process (null when none, or the simulation runs another process). */
export function useSimulationView(process: string): SimulationView | null {
  const v = useSyncExternalStore(subscribe, simulationView, simulationView);
  return v && v.process === process ? v : null;
}
