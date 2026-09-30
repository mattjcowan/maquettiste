// What the explorer asks of the simulation panel (phase-3-design.md 6.1): Simulate opens the process editor on its
// Chart tab with the panel shown, and New scenario… with "Record from simulation" also starts the panel recording
// under the name the dialog was given. The process editor clears the explorer's ProcessFocus as soon as it has
// switched tabs, so the request lives here until the panel of that process consumes it.
import { useSyncExternalStore } from "react";

export interface SimulationRequest {
  process: string;
  /** Start recording a scenario of this name (the Record dialog opens with it prefilled). */
  record?: { name: string };
  /** Grows with each request, so asking twice acts twice. */
  seq: number;
}

let current: SimulationRequest | null = null;
let seq = 0;
const listeners = new Set<() => void>();
const emit = () => {
  for (const l of listeners) l();
};

/** Asks the panel of `process` to show itself, and to start recording when `record` is given. */
export function requestSimulation(process: string, options: { record?: { name: string } } = {}): SimulationRequest {
  current = { process, ...(options.record ? { record: { name: options.record.name } } : {}), seq: ++seq };
  emit();
  return current;
}

export function simulationRequest(): SimulationRequest | null {
  return current;
}

/** Drops the request once the panel acted on it (only when it is still the same one). */
export function consumeSimulationRequest(request: SimulationRequest): void {
  if (current !== request) return;
  current = null;
  emit();
}

const subscribe = (l: () => void) => {
  listeners.add(l);
  return () => listeners.delete(l);
};

/** The pending request for one process (null when it is for another, or none). */
export function useSimulationRequest(process: string): SimulationRequest | null {
  const request = useSyncExternalStore(subscribe, simulationRequest, simulationRequest);
  return request && request.process === process ? request : null;
}
