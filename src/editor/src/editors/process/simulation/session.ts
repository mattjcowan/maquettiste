// The simulation session (phase-3-design.md 4.4 "Simulation state lives in the client", 6.4): the start (context
// values over the defaults and the clock's start) and the inputs raised so far, plus the last answer of the engine.
// Every change produces a new session; the panel sends the whole input list with each call, so the engine replays it
// from the start and nothing is kept on the server. Selecting a trace entry shows the state after it from the trace
// the last call already returned (no second call). Pure: no React, no I/O.
import type { Diagnostic } from "@/api/types";
import type { ScenarioDoc } from "@/model/process";
import type { SimInput, SimulateRequest, SimulationResult, StepTrace } from "./api";
import type { SimulationView } from "./view";

export interface SimStart {
  /** Context values by attribute id, over the attribute defaults (only the values the user set). */
  context: Record<string, unknown>;
  /** The clock's start (ISO 8601); the engine's fixed instant when absent. */
  at?: string;
}

export interface Session {
  process: string;
  start: SimStart;
  inputs: SimInput[];
  /** The trace index shown: null follows the last input; -1 is the initial entry. */
  selected: number | null;
  /** The engine's last answer for exactly this start and these inputs, or null while a call is due. */
  result: SimulationResult | null;
  /** The diagnostics of a draft that does not validate (the engine ran nothing). */
  invalid: Diagnostic[] | null;
  /** A failed call's message. */
  error: string | null;
  /** Grows with every change of the start or the inputs: each simulate call is tagged with it, and an answer for an
   * older version (asked for other inputs) is dropped instead of shown. */
  version: number;
}

export function newSession(process: string, start: SimStart = { context: {} }): Session {
  return { process, start, inputs: [], selected: null, result: null, invalid: null, error: null, version: 0 };
}

/** The session with other inputs or another start: the previous answer no longer describes it. */
function changed(s: Session, patch: Partial<Session>): Session {
  return { ...s, ...patch, selected: null, result: null, invalid: null, error: null, version: (s.version ?? 0) + 1 };
}

/**
 * Adds an input. When an earlier trace entry is shown, the inputs after it are dropped first: raising from the past
 * branches the session there, as a scenario is a single path.
 */
export function addInput(s: Session, input: SimInput): Session {
  const keep = s.selected === null ? s.inputs : s.inputs.slice(0, s.selected + 1);
  return changed(s, { inputs: [...keep, clean(input)] });
}

/** Removes the input at `index` and every input after it (index -1 or below removes them all). */
export function removeFrom(s: Session, index: number): Session {
  if (index >= s.inputs.length) return s;
  return changed(s, { inputs: s.inputs.slice(0, Math.max(0, index)) });
}

/** Shows the state after the input at `index` (-1: the initial entry); the last input (or null) follows new results. */
export function selectEntry(s: Session, index: number | null): Session {
  const selected = index === null || index >= s.inputs.length - 1 ? null : Math.max(-1, index);
  return selected === s.selected ? s : { ...s, selected };
}

/** Back to the start: no inputs, the same start. */
export function resetSession(s: Session): Session {
  return s.inputs.length || s.selected !== null ? changed(s, { inputs: [] }) : s;
}

/** A new start restarts the session: its inputs are dropped. */
export function restart(s: Session, start: SimStart): Session {
  return changed(s, { start: { context: { ...start.context }, ...(start.at ? { at: start.at } : {}) }, inputs: [] });
}

/** Sets one context value of the start (undefined: back to the default), which restarts the session. */
export function setStartValue(s: Session, attribute: string, value: unknown): Session {
  const context = { ...s.start.context };
  if (value === undefined) delete context[attribute];
  else context[attribute] = value;
  return restart(s, { ...s.start, context });
}

/** Loads a scenario's start and steps as the session's start and inputs (From scenario…). */
export function loadScenario(s: Session, scenario: Pick<ScenarioDoc, "start" | "steps">): Session {
  const start: SimStart = { context: { ...(scenario.start?.context ?? {}) }, ...(scenario.start?.at ? { at: scenario.start.at } : {}) };
  return changed(s, { start, inputs: scenario.steps.map((step) => clean(step as SimInput)) });
}

export function withResult(s: Session, result: SimulationResult): Session {
  return { ...s, result, invalid: null, error: null };
}

export function withInvalid(s: Session, diagnostics: Diagnostic[]): Session {
  return { ...s, result: null, invalid: diagnostics, error: null };
}

export function withError(s: Session, error: string): Session {
  return { ...s, result: null, invalid: null, error };
}

/** An answer of the call made for `version`: applied when the session still has that version, else dropped. */
export function withAnswer(
  s: Session,
  version: number,
  answer: { kind: "result"; result: SimulationResult } | { kind: "invalid"; diagnostics: Diagnostic[] } | { kind: "error"; message: string },
): Session {
  if (version !== (s.version ?? 0)) return s;
  if (answer.kind === "result") return withResult(s, answer.result);
  if (answer.kind === "invalid") return withInvalid(s, answer.diagnostics);
  return withError(s, answer.message);
}

/** An input as the session keeps and sends it: no id (the engine numbers inputs), no expectation, no empty members. */
export function clean(input: SimInput & { expect?: unknown }): SimInput {
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(input)) {
    if (k === "id" || k === "expect" || k === "description") continue;
    if (v === undefined || v === null || v === "") continue;
    if (typeof v === "object" && !Array.isArray(v) && Object.keys(v as object).length === 0) continue;
    out[k] = v;
  }
  if (out.input === "event") delete out.input;
  return out as SimInput;
}

/** The simulate request for the session: the whole input list from the start, with the draft when there is one. */
export function requestOf(s: Session, document?: unknown): SimulateRequest {
  const start = { context: s.start.context, ...(s.start.at ? { at: s.start.at } : {}) };
  return { ...(document ? { document: document as never } : {}), start, steps: s.inputs, from: -1 };
}

/** The trace entry shown: the selected one, else the last one the engine ran (null before the first answer). */
export function shownEntry(s: Session): StepTrace | null {
  const trace = s.result?.trace ?? [];
  if (!trace.length) return null;
  if (s.selected !== null) return trace.find((t) => t.index === s.selected) ?? trace[trace.length - 1];
  return trace[trace.length - 1];
}

/** Whether the panel shows where the session stands now (the last entry), so new inputs can be raised from it. */
export function atEnd(s: Session): boolean {
  return s.selected === null;
}

/** The transitions an entry took, across its microsteps, in order; none when it was refused. */
export function takenBy(entry: StepTrace | null): string[] {
  if (!entry || !entry.accepted) return [];
  return entry.microsteps.flatMap((m) => m.transitions);
}

/** What the canvas shows for the session (view.ts): the shown entry's configuration and the transitions it took. */
export function viewOf(s: Session): Omit<SimulationView, "seq"> | null {
  const entry = shownEntry(s);
  if (!entry) return null;
  return { process: s.process, configuration: entry.configuration, taken: takenBy(entry), step: entry.index };
}
